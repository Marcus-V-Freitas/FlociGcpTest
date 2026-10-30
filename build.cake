using System.Globalization;
using System.Xml.Linq;

var target = Argument("target", "Default");
var configuration = Argument("configuration", "Release");
var integration = Argument("integration", true);
var repositoryRoot = Context.GetCallerInfo().SourceFilePath.GetDirectory();
var solution = repositoryRoot.CombineWithFilePath("FlociGcpTest.slnx").FullPath;
var testProject = repositoryRoot.CombineWithFilePath("tests/Floci.Hosting.Gcp.Tests/Floci.Hosting.Gcp.Tests.csproj").FullPath;
var artifactRoot = repositoryRoot.Combine("artifacts");
var coverageDirectory = artifactRoot.Combine("coverage");
var reportDirectory = artifactRoot.Combine("coverage-report");
var resultsDirectory = artifactRoot.Combine("test-results");
var coverageFile = coverageDirectory.CombineWithFilePath("coverage.cobertura.xml");

int RunDotNet(ProcessArgumentBuilder arguments)
{
    return StartProcess("dotnet", new ProcessSettings { Arguments = arguments, WorkingDirectory = repositoryRoot });
}

void CheckExitCode(int exitCode, string operation)
{
    if (exitCode != 0)
        throw new Exception($"{operation} falhou (código {exitCode}).");
}

void CleanArtifactDirectory(DirectoryPath path)
{
    var absolutePath = MakeAbsolute(path);
    if (!absolutePath.FullPath.StartsWith(artifactRoot.FullPath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase))
        throw new Exception($"A limpeza deve permanecer dentro de {artifactRoot}.");
    CleanDirectory(absolutePath);
}

ProcessArgumentBuilder TestArguments()
{
    var arguments = new ProcessArgumentBuilder()
        .Append("test").AppendQuoted(testProject)
        .Append("--configuration").AppendQuoted(configuration)
        .Append("--no-build --no-restore --logger trx")
        .Append("--results-directory").AppendQuoted(resultsDirectory.FullPath);
    if (!integration)
        arguments.Append("--filter").AppendQuoted("Category!=Integration");
    return arguments;
}

void GenerateReport()
{
    if (!FileExists(coverageFile))
        throw new Exception("O coletor não gerou coverage.cobertura.xml.");

    var arguments = new ProcessArgumentBuilder()
        .Append("tool run reportgenerator --")
        .AppendQuoted("-reports:" + coverageFile.FullPath)
        .AppendQuoted("-targetdir:" + reportDirectory.FullPath)
        .AppendQuoted("-reporttypes:Html;Cobertura;MarkdownSummaryGithub;TextSummary")
        .AppendQuoted("-assemblyfilters:+Floci.Hosting.Gcp;+FlociGcpTest.ApiService;+FlociGcpTest.AppHost");
    CheckExitCode(RunDotNet(arguments), "ReportGenerator");

    // Use the same consolidated metrics as the HTML report (deduplicated modules and source lines).
    var coverage = XDocument.Load(reportDirectory.CombineWithFilePath("Cobertura.xml").FullPath).Root;
    if (coverage == null)
        throw new Exception("O relatório de cobertura está vazio.");
    var lines = double.Parse(coverage.Attribute("line-rate").Value, CultureInfo.InvariantCulture);
    var branches = double.Parse(coverage.Attribute("branch-rate").Value, CultureInfo.InvariantCulture);
    Information("Cobertura de linhas: {0:P2}; ramos: {1:P2}", lines, branches);
    Information("Relatório HTML: {0}", reportDirectory.CombineWithFilePath("index.html"));
}

Task("Clean").Does(() =>
{
    CleanArtifactDirectory(coverageDirectory);
    CleanArtifactDirectory(reportDirectory);
    CleanArtifactDirectory(resultsDirectory);
});

Task("Restore").Does(() =>
{
    CheckExitCode(RunDotNet(new ProcessArgumentBuilder().Append("tool restore")), "Restauração de ferramentas");
    CheckExitCode(RunDotNet(new ProcessArgumentBuilder().Append("restore").AppendQuoted(solution)), "Restauração de pacotes");
});

Task("Build").IsDependentOn("Restore").Does(() =>
{
    CheckExitCode(RunDotNet(new ProcessArgumentBuilder().Append("build").AppendQuoted(solution)
        .Append("--configuration").AppendQuoted(configuration).Append("--no-restore")), "Build");
});

Task("Test").IsDependentOn("Build").Does(() =>
{
    CleanArtifactDirectory(resultsDirectory);
    CheckExitCode(RunDotNet(TestArguments()), "Testes");
});

Task("Test-Coverage").IsDependentOn("Build").Does(() =>
{
    CleanArtifactDirectory(coverageDirectory);
    CleanArtifactDirectory(reportDirectory);
    CleanArtifactDirectory(resultsDirectory);
    var arguments = new ProcessArgumentBuilder()
        .Append("tool run dotnet-coverage -- collect")
        .Append("--settings").AppendQuoted(repositoryRoot.CombineWithFilePath("coverage.config.xml").FullPath)
        .Append("--output-format cobertura --output").AppendQuoted(coverageFile.FullPath)
        .Append("dotnet")
        .Append(TestArguments().Render());
    var exitCode = RunDotNet(arguments);
    if (FileExists(coverageFile))
        GenerateReport();
    CheckExitCode(exitCode, "Testes com cobertura");
    if (!FileExists(coverageFile))
        throw new Exception("Os testes terminaram sem gerar o relatório de cobertura.");
});

Task("Report").Does(GenerateReport);
Task("Default").IsDependentOn("Test-Coverage");
RunTarget(target);
