using System;

namespace StableCube.SassCompiler;

public class SassCompilerOptions 
{
    /// <summary>
    /// List of directories to watch for changes with path relative to project root
    /// </summary>
    public HashSet<string> DirectoriesToWatch { get; set; } = [];
    
    /// <summary>
    /// List of files to watch for changes with path relative to project root
    /// </summary>
    public HashSet<string> FilesToWatch { get; set; } = [];

    /// <summary>
    /// When a change is detected this file is recompiled
    /// </summary>
    public string CompileTargetFile { get; set; }

    public string FileSearchPattern { get; set; } = "*.scss";

    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(3);

    public string DartBinaryPath { get; set; } = "sass";
}
