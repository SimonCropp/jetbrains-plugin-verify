using System.Collections.Generic;
using System.IO;
using System.Linq;
using JetBrains.Application.DataContext;
using JetBrains.Application.UI.Actions;
using JetBrains.Application.UI.ActionsRevised.Menu;
using JetBrains.Application.UI.ActionSystem.ActionsRevised.Menu;
using JetBrains.ReSharper.UnitTestFramework.Execution;
using JetBrains.Util;
using VerifyTests.ExceptionParsing;
#if RESHARPER
using JetBrains.ReSharper.UnitTestExplorer.Session.Actions;
using JetBrains.ReSharper.UnitTestFramework.UI.Session.Actions;
#endif

namespace ReSharperPlugin.Verify;

[Action(
    ResourceType: typeof(Resources),
    TextResourceName: nameof(Resources.VerifyAcceptActionText),
    Icon = typeof(Icons.VerifyThemedIcons.VerifyAccept))]
public class VerifyAcceptAction : VerifyAcceptActionBase
{
}

public abstract class VerifyAcceptActionBase :
#if RESHARPER
    IInsertBefore<UnitTestSessionContextMenuActionGroup, UnitTestSessionAppendChildren>,
#endif
    IExecutableAction,
    IActionWithUpdateRequirement
{
    public IActionRequirement GetRequirement(IDataContext dataContext) =>
        dataContext.GetRequirement();

    public bool Update(IDataContext context, ActionPresentation presentation, DelegateUpdate nextUpdate) =>
        context.HasPendingAccept();

    public virtual void Execute(IDataContext context, DelegateExecute nextExecute)
    {
        var resultManager = context.GetComponent<IUnitTestResultManager>();

        var accepted = false;
        var failures = new List<string>();
        var lookup = new InlineLookup();

        foreach (var (result, _) in context.GetVerifyResults(failures))
        {
            foreach (var file in result.New.Concat(result.NotEqual))
            {
                accepted |= Accept(file);
            }

            foreach (var file in result.Delete)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    accepted = true;
                }
            }

            // An inline snapshot lives in the test source file, so accepting it splices the new
            // text into that file rather than moving a received file over a verified one.
            foreach (var entry in result.InlineEntries())
            {
                accepted |= InlineSnapshots.TryAccept(entry, lookup, failures);
            }
        }

        // Accept any remaining snapshots recorded in Verify's received maps. A single test can leave
        // many received files (one per Verify call), while the exception yields only the first pair,
        // or none when the test aggregates its failures. The maps capture every received file with its
        // verified target. Pairs already accepted above are skipped, since their received file is gone.
        foreach (var file in context.GetReceivedMaps())
        {
            accepted |= Accept(file);
        }

        if (accepted)
        {
            foreach (var element in context.GetContextElements())
            {
                resultManager.MarkOutdated(element);
            }
        }

        if (failures.Count > 0)
        {
            MessageBox.ShowError(string.Join("\n\n", failures));
        }
    }

    private static bool Accept(FilePair file)
    {
        if (!File.Exists(file.Received))
        {
            return false;
        }

        if (File.Exists(file.Verified))
        {
            File.Delete(file.Verified);
        }

        File.Move(file.Received, file.Verified);
        return true;
    }
}
