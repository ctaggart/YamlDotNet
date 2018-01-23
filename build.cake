#tool "nuget:?package=xunit.runner.console"
#tool "nuget:?package=Mono.TextTransform"
#tool "nuget:?package=GitVersion.CommandLine"
#tool "nuget:?package=Cake.Incubator"

using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;

//////////////////////////////////////////////////////////////////////
// ARGUMENTS
//////////////////////////////////////////////////////////////////////

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release-Unsigned");
var buildVerbosity = (Verbosity)Enum.Parse(typeof(Verbosity), Argument("buildVerbosity", "Minimal"), ignoreCase: true);

//////////////////////////////////////////////////////////////////////
// PREPARATION
//////////////////////////////////////////////////////////////////////

var solutionPath = "./YamlDotNet.sln";

var releaseConfigurations = new List<string>
{
    "Release-Unsigned",
    "Release-Signed"
};

if (!IsRunningOnWindows())
{
    // AOT requires mono
    releaseConfigurations.Add("Debug-AOT");
}

var packageTypes = new[] { "Unsigned", "Signed" };

var nugetVersion = "0.0.1";

//////////////////////////////////////////////////////////////////////
// TASKS
//////////////////////////////////////////////////////////////////////

Task("Clean")
    .Does(() =>
    {
        CleanDirectories(new[]
        {
            "./YamlDotNet/bin",
            "./YamlDotNet.AotTest/bin",
            "./YamlDotNet.Samples/bin",
            "./YamlDotNet.Test/bin",
            "./YamlDotNet/obj",
            "./YamlDotNet.AotTest/obj",
            "./YamlDotNet.Samples/obj",
            "./YamlDotNet.Test/obj",
        });
    });

Task("Restore-NuGet-Packages")
    .IsDependentOn("Clean")
    .Does(() =>
    {
        NuGetRestore(solutionPath);
    });

Task("Set-Build-Version")
    .Does(() =>
    {
        var version = GitVersion(new GitVersionSettings
        {
            UpdateAssemblyInfo = false
        });
        nugetVersion = version.NuGetVersion;

        if(AppVeyor.IsRunningOnAppVeyor)
        {
            if (!string.IsNullOrEmpty(version.PreReleaseTag))
            {
                nugetVersion = string.Format("{0}-{1}{2}", version.MajorMinorPatch, version.PreReleaseLabel, AppVeyor.Environment.Build.Version.Replace("0.0.", "").PadLeft(4, '0'));
            }
            AppVeyor.UpdateBuildVersion(nugetVersion);
        }
    });

Task("Build")
    .IsDependentOn("Restore-NuGet-Packages")
    .Does(() =>
    {
        BuildSolution(solutionPath, configuration, buildVerbosity);
    });

Task("Test")
    .IsDependentOn("Build")
    .Does(() =>
    {
        RunUnitTests(configuration);
    });

Task("Build-Release-Configurations")
    .IsDependentOn("Restore-NuGet-Packages")
    .IsDependentOn("Set-Build-Version")
    .Does(() =>
    {
        foreach(var releaseConfiguration in releaseConfigurations)
        {
            Information("");
            Information("----------------------------------------");
            Information("Building {0}", releaseConfiguration);
            Information("----------------------------------------");
            BuildSolution(solutionPath, releaseConfiguration, buildVerbosity);
        }
    });

Task("Test-Release-Configurations")
    .IsDependentOn("Build-Release-Configurations")
    .Does(() =>
    {
        foreach(var releaseConfiguration in releaseConfigurations)
        {
            if (releaseConfiguration.EndsWith("-Signed"))
            {
                Information("Skipping signed builds. Configuration: " + releaseConfiguration);
                continue;
            }

            if (releaseConfiguration.Equals("Debug-AOT"))
            {
                RunProcess("mono", "--aot=full", "YamlDotNet.AotTest/bin/Debug/YamlDotNet.dll");
                RunProcess("mono", "--aot=full", "YamlDotNet.AotTest/bin/Debug/YamlDotNet.AotTest.exe");
                RunProcess("mono", "--full-aot", "YamlDotNet.AotTest/bin/Debug/YamlDotNet.AotTest.exe");
            }
            else
            {
                RunUnitTests(releaseConfiguration);
            }
        }
    });

Task("Package")
    .IsDependentOn("Test-Release-Configurations")
    .Does(() =>
    {
        foreach(var packageType in packageTypes)
        {
            // Replace directory separator char
            var baseNuspecFile = "YamlDotNet/YamlDotNet." + packageType + ".nuspec";
            var nuspec = System.IO.File.ReadAllText(baseNuspecFile);

            var finalNuspecFile = baseNuspecFile + ".tmp";
            nuspec = nuspec.Replace('\\', System.IO.Path.DirectorySeparatorChar);
            System.IO.File.WriteAllText(finalNuspecFile, nuspec);

            NuGetPack(finalNuspecFile, new NuGetPackSettings
            {
                Version = nugetVersion,
                OutputDirectory = Directory("YamlDotNet/bin"),
            });
        }
    });

Task("Document")
    .IsDependentOn("Build")
    .Does(() =>
    {
        var samplesBinDir = "YamlDotNet.Samples/bin/" + configuration;
        var testAssemblyFileName = samplesBinDir + "/YamlDotNet.Samples.dll";

        var samplesAssembly = Assembly.LoadFrom(testAssemblyFileName);

        XUnit2(testAssemblyFileName, new XUnit2Settings
        {
            OutputDirectory = Directory(samplesBinDir),
            XmlReport = true
        });

        var samples = XDocument.Load(samplesBinDir + "/YamlDotNet.Samples.dll.xml")
            .Descendants("test")
            .Select(e => new
            {
                Title = e.Attribute("name").Value,
                Type = samplesAssembly.GetType(e.Attribute("type").Value),
                Method = e.Attribute("method").Value,
                Output = e.Element("output") != null ? e.Element("output").Value : null,
            });

        var sampleList = new StringBuilder();

        foreach (var sample in samples)
        {
            var fileName = sample.Type.Name;
            Information("Generating sample documentation page for {0}", fileName);

            var code = System.IO.File.ReadAllText("YamlDotNet.Samples/" + fileName + ".cs");

            var sampleAttr = sample.Type
                .GetMethod(sample.Method)
                .GetCustomAttributes()
                .Single(a => a.GetType().Name == "SampleAttribute");

            var description = UnIndent((string)sampleAttr.GetType().GetProperty("Description").GetValue(sampleAttr, null));

            var samplePage = TransformTextFile("YamlDotNet.Samples/build/SampleTransform.md")
                .WithToken("title", sample.Title)
                .WithToken("description", description)
                .WithToken("code", code)
                .WithToken("output", sample.Output)
                .ToString();

            System.IO.File.WriteAllText("../YamlDotNet.wiki/Samples." + fileName + ".md", samplePage);

            sampleList
                .AppendFormat("* *[{0}](Samples.{1})*  \n", sample.Title, fileName)
                .AppendFormat("  {0}\n", description.Replace("\n", "\n  "));
        }

        var sampleIndexPage = TransformTextFile("YamlDotNet.Samples/build/SampleIndexTransform.md")
            .WithToken("sampleList", sampleList.ToString())
            .ToString();

        System.IO.File.WriteAllText("../YamlDotNet.wiki/Samples.md", sampleIndexPage);
    });

//////////////////////////////////////////////////////////////////////
// TASK TARGETS
//////////////////////////////////////////////////////////////////////

Task("Default")
     // .IsDependentOn("Document");
     .IsDependentOn("Test");

//////////////////////////////////////////////////////////////////////
// EXECUTION
//////////////////////////////////////////////////////////////////////

RunTarget(target);

//////////////////////////////////////////////////////////////////////
// HELPERS
//////////////////////////////////////////////////////////////////////

string UnIndent(string text)
{
    var lines = text
        .Split('\n')
        .Select(l => l.TrimEnd('\r', '\n'))
        .SkipWhile(l => l.Trim(' ', '\t').Length == 0)
        .ToList();

    while (lines.Count > 0 && lines[lines.Count - 1].Trim(' ', '\t').Length == 0)
    {
        lines.RemoveAt(lines.Count - 1);
    }

    if (lines.Count > 0)
    {
        var indent = Regex.Match(lines[0], @"^(\s*)");
        if (!indent.Success)
        {
            throw new ArgumentException("Invalid indentation");
        }

        lines = lines
            .Select(l => l.Substring(indent.Groups[1].Length))
            .ToList();
    }

    return string.Join("\n", lines.ToArray());
}

void BuildSolution(string solutionPath, string configuration, Verbosity verbosity)
{
    const string appVeyorLogger = @"""C:\Program Files\AppVeyor\BuildAgent\Appveyor.MSBuildLogger.dll""";
    MSBuild(solutionPath, settings =>
    {
        if (System.IO.File.Exists(appVeyorLogger)) settings.WithLogger(appVeyorLogger);

        if(IsRunningOnUnix())
        {
            settings.ToolPath = "/usr/bin/msbuild";
        }

        settings
            .SetVerbosity(verbosity)
            .SetConfiguration(configuration)
            .WithProperty("Version", nugetVersion);
    });
}

void RunProcess(string processName, params string[] arguments)
{
    var exitCode = StartProcess(processName, new ProcessSettings().WithArguments(a =>
    {
        foreach (var argument in arguments)
        {
            a.Append(argument);
        }
    }));

    if (exitCode != 0)
    {
        throw new Exception(string.Format("{0} failed with exit code {1}", processName, exitCode));
    }
}

void RunUnitTests(string configurationName)
{
    if (configurationName.Contains("DotNetStandard"))
    {
        // Execute .NETCoreApp tests using `dotnet test`.
        var settings = new DotNetCoreTestSettings
        {
            Framework = "netcoreapp1.0",
            Configuration = configurationName,
            NoBuild = true
        };

        // if (AppVeyor.IsRunningOnAppVeyor)
        // {
        //     settings.ArgumentCustomization = args => args.Append("-appveyor");
        // }

        var path = MakeAbsolute(File("./YamlDotNet.Test/YamlDotNet.Test.csproj"));
        DotNetCoreTest(path.FullPath, settings);
    }
    else
    {
        // Execute the full framework tests using xunit.console.runner.
        // var settings = new XUnit2Settings
        // {
        //     Parallelism = ParallelismOption.All
        // };

        // if (AppVeyor.IsRunningOnAppVeyor)
        // {
        //     settings.ArgumentCustomization = args => args.Append("-appveyor");
        // }

        XUnit2("YamlDotNet.Test/bin/" + configurationName + "/net452/YamlDotNet.Test*.dll");
    }
}
