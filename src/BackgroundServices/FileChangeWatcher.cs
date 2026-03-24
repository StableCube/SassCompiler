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
    private readonly System.Timers.Timer _changeCheckTimer = new ();
    private static bool _isChangeCheckRunning = false;
    private readonly ILogger<FileChangeWatcher> _logger;
    private readonly SassCompilerOptions _options;
    private readonly Dictionary<string, string> _watchedFiles = [];
    private static bool _needsCompile = false;
    private readonly System.Timers.Timer _compileCheckTimer = new ();
    private readonly TimeSpan _compileCheckInterval = TimeSpan.FromSeconds(1);
    private static bool _isCompileRunning = false;
    private string _compileTargetCssPath;

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

        _changeCheckTimer.Elapsed += new ElapsedEventHandler(RunChangeCheck);
        _changeCheckTimer.Interval = _options.PollingInterval.TotalMilliseconds;
        _changeCheckTimer.Enabled = true;

        if (_cancellationToken.IsCancellationRequested == true)
            _changeCheckTimer.Enabled = false;


        _compileCheckTimer.Elapsed += new ElapsedEventHandler(RunCompileCheck);
        _compileCheckTimer.Interval = _compileCheckInterval.TotalMilliseconds;
        _compileCheckTimer.Enabled = true;

        if (_cancellationToken.IsCancellationRequested == true)
            _compileCheckTimer.Enabled = false;

        return Task.CompletedTask;
    }

    private async void RunChangeCheck(object source, ElapsedEventArgs eventArgs)
    {
        if(_isChangeCheckRunning)
            return;

        _isChangeCheckRunning = true;

        var filesToCheck = BuildFileCheckList();
        
        foreach (var filePath in filesToCheck)
        {
            var hash = await GetFileHashAsync(filePath, _cancellationToken);
            if(_watchedFiles.TryGetValue(filePath, out string existingHash))
            {
                if(existingHash != hash)
                {
                    OnChanged(filePath);

                    _watchedFiles[filePath] = hash;
                }
            }
            else
            {
                OnNewFileDetected(filePath);

                _watchedFiles.Add(filePath, hash);
            }
        }

        _isChangeCheckRunning = false;
    }

    private async void RunCompileCheck(object source, ElapsedEventArgs eventArgs)
    {
        if(string.IsNullOrEmpty(_compileTargetCssPath))
            _needsCompile = true;
        
        if(_isCompileRunning || !_needsCompile)
            return;

        _needsCompile = false;
        _isCompileRunning = true;

        if(string.IsNullOrEmpty(_options.CompileTargetFile))
            _logger.LogError("No compile target defined in CompileTargetFile");

        var rootPath = Path.GetDirectoryName(_options.CompileTargetFile);
        var filenameWithoutExt = Path.GetFileNameWithoutExtension(_options.CompileTargetFile);
        var sassFilename = Path.GetFileName(_options.CompileTargetFile);
        var cssFilename = $"{filenameWithoutExt}.css";
        var inputFilePath = Path.Combine(rootPath, sassFilename);
        var outputFilePath = Path.Combine(rootPath, cssFilename);

        await CompileSassAsync(inputFilePath, outputFilePath, _cancellationToken);

        _isCompileRunning = false;
    }

    private static async Task<string> GetFileHashAsync(string filePath, CancellationToken cancellationToken = default)
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
        Queue<string> queue = new();
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

    private void OnChanged(string sourceSassPath)
    {
        var sassFilename = Path.GetFileName(sourceSassPath);

        _logger.LogDebug("Detected file change: {SassFilename}", sassFilename);

        _needsCompile = true;
    }

    private void OnNewFileDetected(string sourceSassPath)
    {
        if(string.IsNullOrEmpty(_compileTargetCssPath))
            return;
        
        var rootPath = Path.GetDirectoryName(sourceSassPath);
        var sassFilename = Path.GetFileName(sourceSassPath);
        var inputFilePath = Path.Combine(rootPath, sassFilename);
        var outputFilePath = Path.Combine(rootPath, _compileTargetCssPath);

        var sourceLastChange = File.GetLastWriteTimeUtc(inputFilePath);
        var destLastChange = File.GetLastWriteTimeUtc(outputFilePath);
        if(sourceLastChange > destLastChange)
        {
            _logger.LogDebug("Detected file change: {SassFilename}", sassFilename);
            _needsCompile = true;
        }
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
