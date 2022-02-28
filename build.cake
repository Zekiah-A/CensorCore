///////////////////////////////////////////////////////////////////////////////
// ARGUMENTS
///////////////////////////////////////////////////////////////////////////////

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var runtimeSpec = Argument<string>("publish-runtimes", "win-x64;osx-x64;linux-x64");

///////////////////////////////////////////////////////////////////////////////
// VERSIONING
///////////////////////////////////////////////////////////////////////////////

var packageVersion = string.Empty;
#load "build/version.cake"

///////////////////////////////////////////////////////////////////////////////
// GLOBAL VARIABLES
///////////////////////////////////////////////////////////////////////////////

var solutionPath = File("./src/CensorCore.sln");
var solution = ParseSolution(solutionPath);
var projects = GetProjects(solutionPath, configuration);
var artifacts = "./dist/";
var modelPath = "./detector_v2_default_checkpoint.onnx";
var runtimes = runtimeSpec.Split(';').ToList();

///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(ctx =>
{
	// Executed BEFORE the first task.
	Information("Running tasks...");
	packageVersion = BuildVersion(fallbackVersion);
});

///////////////////////////////////////////////////////////////////////////////
// TASK DEFINITIONS
///////////////////////////////////////////////////////////////////////////////

Task("Clean")
	.Does(() =>
{
	// Clean solution directories.
	foreach(var path in projects.AllProjectPaths)
	{
		Information("Cleaning {0}", path);
		CleanDirectories(path + "/**/bin/" + configuration);
		CleanDirectories(path + "/**/obj/" + configuration);
	}
	Information("Cleaning common files...");
	CleanDirectory(artifacts);
});

Task("Restore")
	.Does(() =>
{
	// Restore all NuGet packages.
	Information("Restoring solution...");
	foreach (var project in projects.AllProjectPaths) {
		DotNetRestore(project.FullPath);
	}
});

Task("Build")
	.IsDependentOn("Clean")
	.IsDependentOn("Restore")
	.Does(() =>
{
	Information("Building solution...");
	var settings = new DotNetBuildSettings {
		Configuration = configuration,
		NoIncremental = true,
		ArgumentCustomization = args => args
			.Append($"/p:Version={packageVersion}")
			.Append("/p:AssemblyVersion=1.0.0.0")
	};
	DotNetBuild(solutionPath, settings);
});


Task("NuGet")
	.IsDependentOn("Build")
	.Does(() =>
{
	Information("Building NuGet package");
	CreateDirectory(artifacts + "package/");
	var packSettings = new DotNetPackSettings {
		Configuration = configuration,
		NoBuild = true,
		OutputDirectory = $"{artifacts}package",
		ArgumentCustomization = args => args
			.Append($"/p:Version=\"{packageVersion}\"")
			.Append("/p:NoWarn=\"NU1701 NU1602\"")
	};
	foreach(var project in projects.SourceProjectPaths) {
		Information($"Packing {project.GetDirectoryName()}...");
		DotNetPack(project.FullPath, packSettings);
	}
});

Task("Publish-NuGet-Package")
.IsDependentOn("NuGet")
.WithCriteria(() => HasEnvironmentVariable("NUGET_TOKEN"))
.WithCriteria(() => HasEnvironmentVariable("GITHUB_REF"))
.WithCriteria(() => EnvironmentVariable("GITHUB_REF").StartsWith("refs/tags/v") || EnvironmentVariable("GITHUB_REF") == "refs/heads/main")
.Does(() => {
    var nugetToken = EnvironmentVariable("NUGET_TOKEN");
    var pkgFiles = GetFiles($"{artifacts}package/*.nupkg");
	Information($"Pushing {pkgFiles.Count()} package files!");
    NuGetPush(pkgFiles, new NuGetPushSettings {
      Source = "https://api.nuget.org/v3/index.json",
      ApiKey = nugetToken
    });
});

Task("Publish-Runtime")
	.IsDependentOn("Build")
	.Does(() =>
{
	var projectDir = $"{artifacts}server";
	var projPath = "./src/CensorCore.Server/CensorCore.Server.csproj";
	CreateDirectory(projectDir);
	DotNetPublish(projPath, new DotNetPublishSettings {
		OutputDirectory = projectDir + "/dotnet-any",
		Configuration = configuration,
		PublishSingleFile = false,
		PublishTrimmed = false,
		ArgumentCustomization = args => args.Append($"/p:Version={packageVersion}").Append("/p:AssemblyVersion=1.0.0.0")
	});
	foreach (var runtime in runtimes) {
		var runtimeDir = $"{projectDir}/{runtime}";
		CreateDirectory(runtimeDir);
		Information("Publishing for {0} runtime", runtime);
		var settings = new DotNetPublishSettings {
			Runtime = runtime,
			SelfContained = true,
			Configuration = configuration,
			OutputDirectory = runtimeDir,
			// PublishSingleFile = true,
			PublishTrimmed = true,
			// IncludeNativeLibrariesForSelfExtract = true,
			ArgumentCustomization = args => args.Append($"/p:Version={packageVersion}").Append("/p:AssemblyVersion=1.0.0.0")
		};
		DotNetPublish(projPath, settings);
		CleanDirectory(runtimeDir, fsi => fsi.Path.FullPath.EndsWith("onnxruntime.pdb") || fsi.Path.FullPath.EndsWith("onnxruntime.lib"));
		CreateDirectory($"{artifacts}archive");
		Zip(runtimeDir, $"{artifacts}archive/censorcore-server-{runtime}.zip");
	}
});

#load "build/github.cake"
Task("Publish-Console")
	// .IsDependentOn("Download-Model") // not all the while we're not embedding anything!
	.IsDependentOn("Build")
	.Does(() => 
{
	var consoleDir = $"{artifacts}console";
	var projPath = "./src/CensorCore.Console/CensorCore.Console.csproj";
	// var absoluteProjectPath = MakeAbsolute(Directory("./src/CensorCore.Console/"));
	// var absoluteModelPath = MakeAbsolute(File(modelPath));
	// Information($"Absolute: {absoluteProjectPath}");
	// var relModelPath = MakeRelative(absoluteModelPath, absoluteProjectPath);
	// Information($"Relative: {relModelPath}");
	foreach (var runtime in runtimes) {
		// var buildSettings = new DotNetBuildSettings {
		// 	Configuration = configuration,
		// 	NoIncremental = true,
		// 	Runtime = runtime,
		// 	ArgumentCustomization = args => args
		// 		.Append($"/p:Version={packageVersion}")
		// 		.Append("/p:AssemblyVersion=1.0.0.0")
		// 		// .Append($"/p:EmbedModel='embed'")
		// 		.Append("--self-contained")
		// };
		// DotNetBuild(projPath, buildSettings);
		var runtimeDir = $"{consoleDir}/{runtime}";
		CreateDirectory(runtimeDir);
		Information("Publishing for {0} runtime", runtime);
		var settings = new DotNetPublishSettings {
			Runtime = runtime,
			Configuration = configuration,
			OutputDirectory = runtimeDir,
			SelfContained = true,
			PublishSingleFile = true,
			PublishTrimmed = true,
			IncludeNativeLibrariesForSelfExtract = true,
			NoBuild = false,
			ArgumentCustomization = args => args
				.Append($"/p:Version={packageVersion}")
				.Append("/p:AssemblyVersion=1.0.0.0")
				// .Append("/p:EmbedModel='embed'")
		};
		DotNetPublish(projPath, settings);
		CleanDirectory(runtimeDir, fsi => fsi.Path.FullPath.EndsWith("onnxruntime.pdb") || fsi.Path.FullPath.EndsWith("onnxruntime.lib"));
	}
	CreateDirectory($"{artifacts}archive");
	Zip(consoleDir, $"{artifacts}archive/censorcore-console.zip");
});

Task("Default")
	.IsDependentOn("Build");

Task("Publish")
	.IsDependentOn("NuGet")
	.IsDependentOn("Publish-Runtime")
	.IsDependentOn("Publish-Console");

Task("Release")
	.IsDependentOn("Publish")
	.IsDependentOn("Publish-NuGet-Package");

RunTarget(target);



public class ProjectCollection {
	public IEnumerable<SolutionProject> SourceProjects {get;set;}
	public IEnumerable<DirectoryPath> SourceProjectPaths {get { return SourceProjects.Select(p => p.Path.GetDirectory()); } } 
	public IEnumerable<SolutionProject> TestProjects {get;set;}
	public IEnumerable<DirectoryPath> TestProjectPaths { get { return TestProjects.Select(p => p.Path.GetDirectory()); } }
	public IEnumerable<SolutionProject> AllProjects { get { return SourceProjects.Concat(TestProjects); } }
	public IEnumerable<DirectoryPath> AllProjectPaths { get { return AllProjects.Select(p => p.Path.GetDirectory()); } }
}

ProjectCollection GetProjects(FilePath slnPath, string configuration) {
	var solution = ParseSolution(slnPath);
	var projects = solution.Projects.Where(p => p.Type != "{2150E333-8FDC-42A3-9474-1A3956D46DE8}");
	var testAssemblies = projects.Where(p => p.Name.Contains(".Tests")).Select(p => p.Path.GetDirectory() + "/bin/" + configuration + "/" + p.Name + ".dll");
	return new ProjectCollection {
		SourceProjects = projects.Where(p => !p.Name.Contains(".Tests")),
		TestProjects = projects.Where(p => p.Name.Contains(".Tests"))
	};
}