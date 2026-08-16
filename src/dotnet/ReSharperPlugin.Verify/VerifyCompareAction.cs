using System;
using System.Collections.Generic;
using JetBrains.Application.DataContext;
using JetBrains.Application.UI.Actions;
using JetBrains.Application.UI.ActionsRevised.Menu;
using JetBrains.Application.UI.ActionSystem.ActionsRevised.Menu;
using JetBrains.Util;
using System.IO;
using DiffEngine;
#if RESHARPER
using JetBrains.ReSharper.UnitTestExplorer.Session.Actions;
using JetBrains.ReSharper.UnitTestFramework.UI.Session.Actions;
#elif RIDER
using JetBrains.ProjectModel;
using JetBrains.ReSharper.Feature.Services.Protocol;
#endif

namespace ReSharperPlugin.Verify;

[Action(
    ResourceType: typeof(Resources),
    TextResourceName: nameof(Resources.VerifyCompareActionText),
    Icon = typeof(Icons.VerifyThemedIcons.VerifyCompare))]
public class VerifyCompareAction :
#if RESHARPER
    IInsertBefore<UnitTestSessionContextMenuActionGroup, UnitTestSessionAppendChildren>,
#endif
    IExecutableAction,
    IActionWithUpdateRequirement
{
    public IActionRequirement GetRequirement(IDataContext dataContext) =>
        dataContext.GetRequirement();

    public bool Update(IDataContext context, ActionPresentation presentation, DelegateUpdate nextUpdate) =>
        context.HasPendingCompare();

    public void Execute(IDataContext context, DelegateExecute nextExecute)
    {
        var lookup = new InlineLookup();
        // A message that would not parse, and a conflict the diff cannot show. Collected rather
        // than raised as each is found, so several selected tests produce one dialog
        var notes = new List<string>();
        foreach (var (result, element) in context.GetVerifyResults(notes))
        {
#if RIDER
            var verifyTestsModel = context.GetComponent<ISolution>().GetProtocolSolution().GetVerifyModel();
            var presentation = element.GetPresentation();
            // Rider shows text in its own diff view and hands everything else to a diff tool
            Action<string, string> showText = (left, right) =>
                verifyTestsModel.Compare.Fire(new CompareData(presentation, left, right));
#else
            Action<string, string> showText = (left, right) => DiffRunner.Launch(left, right);
#endif

            foreach (var file in result.ReceivedFiles())
            {
#if RIDER
                if (EmptyFiles.FileExtensions.IsTextFile(file.Received))
                {
                    // The diff view needs two files, and a new snapshot has no verified one yet
                    if (!File.Exists(file.Verified))
                    {
                        File.WriteAllText(file.Verified, "");
                    }

                    showText(file.Received, file.Verified);
                    continue;
                }
#endif
                DiffRunner.Launch(file.Received, file.Verified);
            }

            // An inline snapshot has no verified file. What stands in for one is the snapshot the
            // source file holds, and it is always text.
            foreach (var entry in result.InlineEntries())
            {
                if (InlineSnapshots.TryGetTexts(entry, lookup, notes, out var received, out var expected))
                {
                    showText(received, expected);
                }
            }
        }

        if (notes.Count > 0)
        {
            MessageBox.ShowError(string.Join("\n\n", notes));
        }
    }
}
