using System.Timers;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using CliWrap;
using CliWrap.Buffered;

namespace StableCube.SassCompiler;

public class FileChangeWatcher : BackgroundService
{
    private static CancellationToken _cancellationToken;
    private readonly System.Timers.Timer _timer = new ();
    private static bool _isScanRunning = false;
    private readonly ILogger<FileChangeWatcher> _logger;
    private readonly SassCompilerOptions _options;
    private Dictionary<string, string> _watchedFiles = [];

    public FileChangeWatcher(
        ILogger<FileChangeWatcher> logger,
        SassCompilerOptions options
    )
    {
        _logger = logger;
        _options = options;
    }

    protected override Task ExecuteAsync(CancellationToken cancellationToken = default)
    {
        _cancellationToken = cancellationToken;

        _logger.LogInformation("Sass compiler file change watcher starting");

        _timer.Elapsed += new ElapsedEventHandler(RunChangeCheck);
        _timer.Interval = _options.PollingInterval.TotalMilliseconds;
        _timer.Enabled = true;

        if (_cancellationToken.IsCancellationRequested == true)
            _timer.Enabled = false;

        return Task.CompletedTask;
    }

    private async void RunChangeCheck(object source, ElapsedEventArgs eventArgs)
    {
        if(_isScanRunning)
            return;

        _isScanRunning = true;

        var filesToCheck = BuildFileCheckList();
        
        foreach (var filePath in filesToCheck)
        {
            var hash = await GetFileHashAsync(filePath, _cancellationToken);

            if(_watchedFiles.TryGetValue(filePath, out string existingHash))
            {
                if(existingHash != hash)
                {
                    await OnChangedAsync(filePath, _cancellationToken);

                    _watchedFiles[filePath] = hash;
                }
            }
            else
            {
                await OnNewAsync(filePath, _cancellationToken);

                _watchedFiles.Add(filePath, hash);
            }
        }

        _isScanRunning = false;
    }

    private async Task<string> GetFileHashAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var rawBytes = await File.ReadAllBytesAsync(filePath, cancellationToken);
        var hashValue = SHA256.HashData(rawBytes);

        return Convert.ToHexString(hashValue);
    }

    private HashSet<string> BuildFileCheckList()
    {
        HashSet<string> filesToCheck = [];
        foreach (var relativePath in _options.DirectoriesToWatch)
        {
            var fullDirPath = Path.Combine(Environment.CurrentDirectory, relativePath);
            if(!Directory.Exists(fullDirPath))
            {
                _logger.LogError("Directory not found. Skipping {FullDirPath}", fullDirPath);
                continue;
            }

            var files = GetFiles(fullDirPath);
            foreach (var item in files)
                filesToCheck.Add(item);
        }

        foreach (var relativePath in _options.FilesToWatch)
        {
            var fullPath = Path.Combine(Environment.CurrentDirectory, relativePath);
            if(!File.Exists(fullPath))
            {
                _logger.LogError("File not found. Skipping {FullPath}", fullPath);
                continue;
            }

            filesToCheck.Add(fullPath);
        }

        return filesToCheck;
    }

    private IEnumerable<string> GetFiles(string path)
    {
        Queue<string> queue = new Queue<string>();
        queue.Enqueue(path);
        while (queue.Count > 0)
        {
            path = queue.Dequeue();
            try
            {
                foreach (string subDir in Directory.GetDirectories(path))
                {
                    queue.Enqueue(subDir);
                }
            }
            catch(Exception ex)
            {
                Console.Error.WriteLine(ex);
            }

            string[] files = null;
            
            try
            {
                files = Directory.GetFiles(path, _options.FileSearchPattern);
            }
            catch (Exception ex)
            {
                _logger.LogError(exception: ex, message: "Error while building file watch list");
            }
            if (files != null)
            {
                for(int i = 0 ; i < files.Length ; i++)
                {
                    yield return files[i];
                }
            }
        }
    }

    private async Task OnChangedAsync(string sourceSassPath, CancellationToken cancellationToken = default)
    {
        var rootPath = Path.GetDirectoryName(sourceSassPath);
        var filenameWithoutExt = Path.GetFileNameWithoutExtension(sourceSassPath);
        var sassFilename = Path.GetFileName(sourceSassPath);
        var cssFilename = $"{filenameWithoutExt}.css";
        var inputFilePath = Path.Combine(rootPath, sassFilename);
        var outputFilePath = Path.Combine(rootPath, cssFilename);

        _logger.LogDebug("Detected file change: {SassFilename}", sassFilename);

        await CompileSassAsync(inputFilePath, outputFilePath, cancellationToken);
    }

    private async Task OnNewAsync(string sourceSassPath, CancellationToken cancellationToken = default)
    {
        var rootPath = Path.GetDirectoryName(sourceSassPath);
        var filenameWithoutExt = Path.GetFileNameWithoutExtension(sourceSassPath);
        var sassFilename = Path.GetFileName(sourceSassPath);
        var cssFilename = $"{filenameWithoutExt}.css";
        var inputFilePath = Path.Combine(rootPath, sassFilename);
        var outputFilePath = Path.Combine(rootPath, cssFilename);

        bool runCompile = false;
        if(!File.Exists(outputFilePath))
        {
            runCompile = true;
        }
        else
        {
            var sourceLastChange = File.GetLastWriteTimeUtc(inputFilePath);
            var destLastChange = File.GetLastWriteTimeUtc(outputFilePath);
            if(sourceLastChange > destLastChange)
            {
                _logger.LogDebug("Detected file change: {SassFilename}", sassFilename);
                runCompile = true;
            }
        }

        if(runCompile)
            await CompileSassAsync(inputFilePath, outputFilePath, cancellationToken);
    }

    private async Task CompileSassAsync(
        string inputFilePath, 
        string outputFilePath, 
        CancellationToken cancellationToken = default
    )
    {
        BufferedCommandResult result = await Cli.Wrap(_options.DartBinaryPath)
            .WithArguments([
                "--style=compressed",
                "--no-source-map",
                "--stop-on-error",
                inputFilePath,
                outputFilePath
            ])
            .WithWorkingDirectory(Environment.CurrentDirectory)
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(cancellationToken);

        if(result.ExitCode != 0)
        {
            if(result.StandardError != null)
                _logger.LogError("Compile Error, {Error}", result.StandardError);
        }
    }
}
