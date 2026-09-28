using System;
using System.IO;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace ClaudeForVS.Services
{
    internal sealed class EditorSelection
    {
        public string FilePath { get; set; }
        public string Text { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public string Language { get; set; }
    }

    /// <summary>Small helpers around DTE for things the commands need from the IDE.</summary>
    internal static class VsContext
    {
        public static DTE2 Dte
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return (DTE2)Package.GetGlobalService(typeof(DTE));
            }
        }

        /// <summary>Solution folder, "Open Folder" root, or the active document's folder.</summary>
        public static string GetWorkingDirectory()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (Package.GetGlobalService(typeof(SVsSolution)) is IVsSolution solution
                && solution.GetSolutionInfo(out var dir, out _, out _) == 0
                && !string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                return dir;
            }

            try
            {
                var doc = Dte?.ActiveDocument?.FullName;
                if (!string.IsNullOrEmpty(doc)) return Path.GetDirectoryName(doc);
            }
            catch (ArgumentException) { }

            return null;
        }

        public static EditorSelection GetSelection()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var doc = Dte?.ActiveDocument;
            if (!(doc?.Selection is TextSelection selection)) return null;

            var text = selection.Text;
            int start = selection.TopPoint.Line, end = selection.BottomPoint.Line;
            if (string.IsNullOrWhiteSpace(text))
            {
                // Nothing selected: use the whole document.
                var textDoc = (TextDocument)doc.Object("TextDocument");
                var editPoint = textDoc.StartPoint.CreateEditPoint();
                text = editPoint.GetText(textDoc.EndPoint);
                start = 1;
                end = textDoc.EndPoint.Line;
            }

            return new EditorSelection
            {
                FilePath = doc.FullName,
                Text = text,
                StartLine = start,
                EndLine = end,
                Language = doc.Language,
            };
        }

        public static void SetStatus(string text)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar statusBar)
            {
                statusBar.IsFrozen(out var frozen);
                if (frozen == 0) statusBar.SetText(text);
            }
        }
    }
}
