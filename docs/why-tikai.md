# Why Tikai

Tikai lets AI agents write code while people stay in control of what goes into the codebase. This page explains the idea, what Tikai is good for today, and what it still needs for large projects.

## Built for control, not for speed alone

With AI agents, writing code is no longer the hard part. The hard part is trusting the code they write. Most agent tools work on generating code faster. Tikai works on keeping that code under control:

- **Every change starts from a ticket.** The ticket holds the intent and the acceptance criteria. The agent works against it, and the review checks the code against it.
- **Agents ask instead of guessing.** An agent proposes a plan before it starts, and asks a question from inside the ticket when it is unsure. Both reach you in the inbox, and the agent waits for your answer.
- **Nothing merges without a human.** Every change is reviewed and approved by a person. The review screen maps the code to the criteria, orders the files, and marks what needs a close look.
- **Fix rounds stay small.** Comments go back to the agent with *Request changes*. In the next round you only re-read what changed since your last review (the interdiff).
- **Agents run in a sandbox.** Each run has its own container and its own clone of the repository. It gets its own run token, no git credentials, no database and no Docker socket. The hub imports the agent's work and checks every git object, without running git inside the agent's directory.
- **Everything is traceable.** A run keeps its transcript, plan, questions, answers, cost and every review round. The merged commit names the ticket.

Tikai works well on a small project, where skipping review would be fine anyway. It is built for the project where you can't skip review: a large, long-lived codebase where one unreviewed change costs more than it saves.

Tikai was built this way itself: small steps, each with a plan, a branch, a review and a check by a person. Tikai turns that way of working into a product.

## What it is good for today

- **Node and TypeScript projects** work fully. The agent installs packages, builds, lints and runs unit tests before it submits.
- **Other languages** (.NET, Python, Go, Rust, Java) work in part. The agent writes the code, but the agent image has no SDK for them, so the code it submits has not been compiled or tested. Run your tests in your own CI. A good way to do that is **Push the branch** mode with a pull request on GitHub.
- **Tests that need services** (a database, containers, a browser) can't run inside an agent run yet.
- **One run per repository at a time.** Several agents in parallel on one repository come in Phase 2.

## What large projects still need

In order of importance:

1. **The agent checks its own work.** Each project needs its own toolchain and test services inside the run (for example a project-specific agent image). Without them, people do the compiler's job in review.
2. **CI results go back to the agent.** When the agent sees a failed pipeline and fixes it before you look, your review time goes to design, not to broken builds.
3. **Parallel runs on one repository.** Several agents on separate tickets at once, with the merge-conflict and rebase handling Tikai already has.
4. **Review that scales.** With many agents, the person reviewing becomes the bottleneck, not the agent. Trust rules (strict review for core code, light review for tests and docs), small tickets and the review analysis keep review fast enough to stay real.
5. **Breaking down large work.** Splitting an epic into small, dependent tickets that agents can deliver one by one.

The hard groundwork is already there: the sandbox, the review flow, delivery to a remote, and recovery after a restart. These points build on it.
