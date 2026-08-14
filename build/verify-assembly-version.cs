#:property PublishAot=false

using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

if (args.Length != 2)
{
    Fail("usage: dotnet run --file verify-assembly-version.cs -- <plugin.dll> <expected-version>");
}

var assemblyPath = Path.GetFullPath(args[0]);
var expected = args[1];
if (!File.Exists(assemblyPath))
{
    Fail($"assembly not found: {assemblyPath}");
}

if (!Regex.IsMatch(expected, "^[0-9]+\\.[0-9]+\\.[0-9]+(-dev\\.[0-9]+)?$"))
{
    Fail($"invalid expected semantic version: {expected}");
}

var numericVersion = expected.Split('-', 2)[0];
if (!Version.TryParse($"{numericVersion}.0", out var expectedAssemblyVersion))
{
    Fail($"expected version cannot be represented as a numeric assembly version: {expected}");
}

var assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
if (assemblyName.Name != "Shoko.ImagePlanner")
{
    Fail($"assembly name is {assemblyName.Name ?? "<none>"}, expected Shoko.ImagePlanner");
}

if (assemblyName.Version != expectedAssemblyVersion)
{
    Fail($"assembly version is {assemblyName.Version}, expected {expectedAssemblyVersion}");
}

var fileVersionText = FileVersionInfo.GetVersionInfo(assemblyPath).FileVersion;
if (!Version.TryParse(fileVersionText, out var fileVersion) || fileVersion != expectedAssemblyVersion)
{
    Fail($"file version is {fileVersionText ?? "<none>"}, expected {expectedAssemblyVersion}");
}

var assembly = Assembly.Load(File.ReadAllBytes(assemblyPath));
var informationalVersion = assembly
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
    ?.InformationalVersion;
if (informationalVersion != expected)
{
    Fail($"informational version is {informationalVersion ?? "<none>"}, expected {expected}");
}

Console.WriteLine($"OK: assembly versions match {expected} ({expectedAssemblyVersion})");

static void Fail(string message)
{
    Console.Error.WriteLine($"error: {message}");
    Environment.Exit(1);
}
