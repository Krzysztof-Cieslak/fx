# fx

**An AI coding agent for your terminal, built in F#.**

> ⚠️ **Exploration stage.** fx is an early experiment. Nothing here is usable yet. The design is in [`spec.md`](spec.md), and the name will probably change.

## What fx aims to be

- **An agent that works in code.** The agent writes small TypeScript programs that call fx's tools. The values it works with stay in a live session and are passed around by name, so large files and outputs don't fill up its context.
- **Work you can verify.** A turn finishes only with a structured result: what changed, the evidence, and how to check it. Goals are finished only when fx's own checks pass, not when the model says it's done.
- **Decisions that stick.** Choices you make while working with the agent are stored in your repository, follow your branches, and can be reviewed in pull requests. Agents follow them in later sessions.
- **Specialized agents, goals and workflows.** Use reviewer, QA or planner agents, long-running goal loops, and multi-step workflows. All of them show up in one Runs view, and you can define them in Markdown, TypeScript or F#.
- **Runs anywhere.** Sessions can run locally, in a sandbox, over SSH or in cloud sandboxes. Several clients can attach to the same session, and closing one doesn't stop the work.
- **Bring your own model.** Works with OpenAI, Anthropic, Google and OpenRouter, plus local OpenAI-compatible servers, GitHub Copilot, and ChatGPT sign-in.
- **Extensible all the way down.** Environments, models, sign-in methods, interpreters, agents and storage are all extensions. The built-in features use the same API that third-party extensions do.
- **Measured in the open.** Behaviour comes from configurable variants that we evaluate on SWE-bench Verified, Terminal-Bench 2.0 and capability tests. The results are published on a public dashboard.

## Platforms

fx is planned as a single native binary for Linux, macOS and Windows.

## Status

Nothing to install yet. Follow the repository for progress, or read the [design spec](spec.md).

## Privacy

fx collects no telemetry.

## License

[MIT](LICENSE)
