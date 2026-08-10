using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Application.DataContext;
using JetBrains.Application.UI.ActionSystem.ActionsRevised.Menu;
using JetBrains.DocumentModel.DataContext;
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Actions;
using JetBrains.ReSharper.Psi.Files;
using JetBrains.ReSharper.UnitTestFramework.Actions;
using JetBrains.ReSharper.UnitTestFramework.Criteria;
using JetBrains.ReSharper.UnitTestFramework.Elements;
using JetBrains.ReSharper.UnitTestFramework.Execution;
using JetBrains.Util;
using VerifyTests.ExceptionParsing;

public static class Extensions
{
    public static IActionRequirement GetRequirement(this IDataContext dataContext)
    {
        if (dataContext.GetData(DocumentModelDataConstants.DOCUMENT) == null)
        {
            return CommitAllDocumentsRequirement.TryGetInstance(dataContext);
        }

        return CurrentPsiFileRequirement.FromDataContext(dataContext);
    }

    public static bool HasPendingCompare(this IDataContext context)
    {
        foreach (var (result, _) in context.GetVerifyResults())
        {
            foreach (var file in result.New.Concat(result.NotEqual))
            {
                if (File.Exists(file.Received))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public static bool HasPendingAccept(this IDataContext context)
    {
        foreach (var (result, _) in context.GetVerifyResults())
        {
            foreach (var file in result.New.Concat(result.NotEqual))
            {
                if (File.Exists(file.Received))
                {
                    return true;
                }
            }

            foreach (var file in result.Delete)
            {
                if (File.Exists(file))
                {
                    return true;
                }
            }
        }

        // Fall back to the received maps Verify writes to disk. A single test can leave many received
        // files (for example one Verify call per endpoint via Task.WhenAll, or a loop that aggregates
        // the failures), but the exception only carries the first pair, or is not a VerifyException at
        // all. The maps record every received file with its verified target, independent of exceptions.
        foreach (var file in context.GetReceivedMaps())
        {
            if (File.Exists(file.Received))
            {
                return true;
            }
        }

        return false;
    }

    public static IEnumerable<VirtualFileSystemPath> GetVerifiedFiles(this IDataContext context)
    {
        var session = context.GetData(UnitTestDataConstants.Session.CURRENT);
        if (session == null)
        {
            yield break;
        }

        var elements = context.GetData(UnitTestDataConstants.Elements.IN_CONTEXT)?.Criterion.Evaluate();
        if (elements == null)
        {
            yield break;
        }

        foreach (var element in elements)
        {
            var directory = element.GetProjectFiles()?.FirstOrDefault()?.Location.Parent;
            if (directory == null)
                continue;

            var parent = element.TraverseAcross(x => x.Parent).Last().ShortName;
            var name = element.ShortName
                .Replace("(", "_")
                .Replace(": ", "=")
                .Replace(", ", "_")
                .Replace("\"", string.Empty)
                .Replace(")", string.Empty);
            var verifiedFileName = $"{parent}.{name}.verified";

            foreach (var file in directory.GetChildFiles(verifiedFileName + "*"))
            {
                yield return file;
            }
        }
    }

    public static IEnumerable<(Result, IUnitTestElement)> GetVerifyResults(this IDataContext context)
    {
        var session = context.GetData(UnitTestDataConstants.Session.CURRENT);
        if (session == null)
        {
            yield break;
        }

        var elements = context.GetData(UnitTestDataConstants.Elements.IN_CONTEXT)?.Criterion.Evaluate();
        if (elements == null)
        {
            yield break;
        }

        var resultManager = context.GetComponent<IUnitTestResultManager>();

        foreach (var element in elements)
        {
            var result = resultManager.GetResultData(element, session);
            if (!result.HasVerifyException())
            {
                continue;
            }

            var parsed = result.GetParseResult();
            if (!parsed.Equals(default(Result)))
            {
                yield return (parsed, element);
            }
        }
    }

    private static bool HasVerifyException(this UnitTestResultData result)
    {
        if (result.ExceptionCount == 0)
            return false;

        var info = result.GetExceptionInfo(0);
        return info.Type == "VerifyException" ||
               (info.Message?.StartsWith("VerifyException") ?? false) ||
               // MSTest
               (info.Message?.Substring(info.Message.IndexOf('\n') + 1, 15) == "VerifyException");
    }

    private static Result GetParseResult(this UnitTestResultData result)
    {
        var message = result.GetExceptionInfo(0).Message!;
        try
        {
            return Parser.Parse(message);
        }
        catch (Exception exception)
        {
            MessageBox.ShowError(
                exception.Message +
                "\n\nNote that you might need to rerun tests before your changes take effect.");
            return default;
        }
    }

    public static IReadOnlyList<IUnitTestElement> GetContextElements(this IDataContext context)
    {
        var session = context.GetData(UnitTestDataConstants.Session.CURRENT);
        if (session == null)
        {
            return Array.Empty<IUnitTestElement>();
        }

        var elements = context.GetData(UnitTestDataConstants.Elements.IN_CONTEXT)?.Criterion.Evaluate();
        if (elements == null)
        {
            return Array.Empty<IUnitTestElement>();
        }

        return elements.ToList();
    }

    // Reads the received maps Verify writes to the intermediate (obj) directory of each project in
    // context. Unlike the exception message, these cover every received file a test left on disk, so
    // a test that produces many snapshots can be accepted as a whole. Each returned pair is one whose
    // received file still exists (ReceivedMaps drops stale records), deduplicated by received path.
    public static IReadOnlyList<FilePair> GetReceivedMaps(this IDataContext context)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in context.GetContextElements())
        {
            var directory = element.GetProjectDirectory();
            if (directory != null)
            {
                directories.Add(directory);
            }
        }

        if (directories.Count == 0)
        {
            return Array.Empty<FilePair>();
        }

        var pairs = new List<FilePair>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in directories)
        {
            foreach (var pair in ReceivedMaps.Read(directory).Pairs)
            {
                if (seen.Add(pair.Received))
                {
                    pairs.Add(pair);
                }
            }
        }

        return pairs;
    }

    // The project directory holds the obj directory the maps are written under. ReceivedMaps.Read
    // scans it recursively, so the exact intermediate path does not need to be known here.
    private static string GetProjectDirectory(this IUnitTestElement element)
    {
        var project = element.GetProjectFiles()?.FirstOrDefault()?.GetProject();
        if (project == null)
        {
            return null;
        }

        var location = project.Location;
        if (location.IsEmpty)
        {
            return null;
        }

        return location.FullPath;
    }
}
