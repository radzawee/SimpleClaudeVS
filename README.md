# Claude for Visual Studio

Use the Claude subscription you already pay for (Pro, Max, Team or Enterprise) right inside **Visual Studio 2022 and 2026**: commit messages, a chat about your code, and code explanations and reviews. You don't need an API key or a separate bill.

## Description

AI features in Visual Studio usually need their own subscription or a pay-as-you-go API key. If you already have a Claude plan, this extension lets you use it in the IDE.

The extension doesn't call the Anthropic API directly. It runs the official [Claude Code CLI](https://claude.com/claude-code) in the background, and the CLI is already signed in with your Claude account. So:

- **Everything counts against your subscription's usage**, the same as using Claude Code in a terminal. There are no per-token charges.
- **Nothing to configure.** If `claude` works in your terminal, the extension works in Visual Studio.
- **You can see how much of your plan you've used.** The status bar shows your current 5-hour and weekly usage after every request, so you can tell when you're close to a limit.

### What it can do

**Write your commit messages.** Stage your changes and choose *Tools › Claude › Generate Commit Message…*. Claude reads the diff, your branch name and your recent commit history, then writes a message in your project's style (Conventional Commits by default). The message streams into an editable dialog, and you can commit with one click, copy it, or ask for another version. If nothing is staged, it describes all working-tree changes and can stage them for you.

**Chat about your code.** A dockable **Claude** window keeps a running conversation that starts in your solution folder. Claude can open and search files in your solution to answer questions ("where is the retry policy configured?", "why might this test be flaky?"). By default it can't edit anything.

**Explain or review a selection.** Right-click in the editor and choose *Claude › Explain Selection*, *Review Selection* or *Ask About Selection…*. The selected code, or the whole file if nothing is selected, goes to the chat along with its file path and line numbers.

**Check your usage.** *Tools › Claude › Check Subscription Usage* shows how much of your 5-hour and weekly limits you've used, and when each resets.

## Features at a glance

| Where | Command | What it does |
|---|---|---|
| **Tools › Claude** | Generate Commit Message… | Diff → message in an editable dialog → **Commit** / **Copy** / **Regenerate** |
| **Tools › Claude** | Open Claude Chat | Dockable chat window; one resumable session per conversation |
| **Tools › Claude** | Check Subscription Usage | 5-hour and weekly usage, with reset times |
| **Editor right-click › Claude** | Explain / Review / Ask About Selection | Sends the code to the chat |
| **Status bar** | – | `Claude usage: 5h 13% · 7d 26%` after each request |

## Requirements

- Visual Studio 2022 (17.x) or 2026 (18.x), x64 or ARM64
- An active Claude subscription (Pro, Max, Team or Enterprise)
- Claude Code CLI, installed and signed in:
  ```powershell
  irm https://claude.ai/install.ps1 | iex   # or: npm i -g @anthropic-ai/claude-code
  claude                                     # then run /login and choose your Claude account
  ```
- Git for Windows on `PATH`

## Getting started

1. Install the CLI and sign in (see above).
2. Double-click `ClaudeForVS.vsix` to install the extension, then restart Visual Studio.
3. Open a solution that's in a git repository, stage some changes, and choose **Tools › Claude › Generate Commit Message…**.

Tip: to add a keyboard shortcut, go to **Tools › Options › Environment › Keyboard** and search for `Claude.GenerateCommitMessage`.

## Settings

Go to **Tools › Options › Claude › General**.

| Setting | Default | Notes |
|---|---|---|
| Claude executable | `claude` | Looked up on `PATH`, `~\.local\bin` and `%APPDATA%\npm` |
| Use subscription login only | On | Removes `ANTHROPIC_API_KEY` / `ANTHROPIC_AUTH_TOKEN` from the CLI's environment so an API key can't switch you to API billing |
| Timeout (seconds) | 300 | Per request |
| Commit messages › Model | `haiku` | Fast and light on usage. Can be `sonnet`, `opus` or a full model name |
| Commit messages › Use Conventional Commits | On | `feat(scope): summary` |
| Commit messages › Include body | On | Short explanation of what and why, for non-trivial changes |
| Commit messages › Extra instructions | – | e.g. "Prefix with the Jira key from the branch name", "Write in Polish" |
| Commit messages › Max diff characters | 80000 | Larger diffs are cut off at this length |
| Chat › Model | CLI default | |
| Chat › Allowed tools | `Read,Grep,Glob` | Read-only. Leave empty for no tools |

## Privacy and usage

- Code is sent to Claude only when you run a command: the diff for commit messages, the selection or file for editor commands, and whatever you type in the chat.
- Commit-message requests run with no tools and aren't saved as sessions. Chat sessions are saved like any Claude Code session, so you can pick one up in a terminal with `claude --resume`.
- Usage numbers come from the CLI's responses, so they update whenever you use a command. *Check Subscription Usage* sends one tiny Haiku request to get fresh numbers.

## Limitations

- Visual Studio has no public API for filling the message box in the **Git Changes** window. The dialog commits for you instead, or copies the message so you can paste it there (Ctrl+V).
- Only Windows is supported, because Visual Studio only runs on Windows.

## Troubleshooting

| Symptom | Fix |
|---|---|
| "Could not start the Claude CLI" | Run `claude --version` in a terminal. If that works, set the full path to `claude.exe` in the settings. |
| Errors about login or auth | Run `claude` in a terminal and `/login` again. |
| "Is not inside a git repository" | Open a solution or folder that's inside a git working tree. |
| Usage shows "unknown" | You're probably signed in with an API key, not a subscription. Run `/login` and choose your Claude account. |

## Building from source

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" ClaudeForVS.sln /restore /p:Configuration=Release
# output: src\ClaudeForVS\bin\Release\net48\ClaudeForVS.vsix
```

The build only needs NuGet packages (`Microsoft.VisualStudio.SDK`, `Microsoft.VSSDK.BuildTools`), not the "Visual Studio extension development" workload. To debug with F5 in the Experimental Instance, install that workload and open `ClaudeForVS.sln`.

### Project layout

```
src/ClaudeForVS/
  ClaudePackage.cs                     package and command handlers
  ClaudePackage.vsct                   menus (Tools › Claude, editor context menu)
  Options/                             Tools › Options page and plain settings object
  Services/ClaudeCli.cs                runs `claude -p`, parses stream-json output and usage events
  Services/CommitMessageGenerator.cs   collects the diff, builds the prompt
  Services/Git.cs, VsContext.cs        git and IDE helpers
  UI/                                  commit dialog, chat tool window, theming
```
