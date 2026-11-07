## message
A human approved round {{round}} of your work, but it does not merge: {{default_branch}} has moved since you started, and these files conflict:
{{files}}

The current {{default_branch}} is in the git bundle {{bundle}}. Work like this:

1. Call get_review_comments: the request to rebase is there as a thread, maybe with more comments.
2. Fetch the current {{default_branch}} and rebase your branch onto it:
   git fetch --quiet {{bundle}} "+refs/heads/{{default_branch}}:refs/remotes/tikai/{{default_branch}}"
   git rebase tikai/{{default_branch}}
   Resolve each conflict so that both the change on {{default_branch}} and yours keep working; do not drop either side. Then git add the files and git rebase --continue until the rebase is done. Do not merge instead of rebasing.
3. Run the checks you ran before (build, tests) and fix what the rebase broke. Call report_progress whenever you start a new step. When a conflict is unclear, call ask_human instead of guessing; it waits for the answer.
4. Answer every thread with reply_to_thread: what conflicted and how you resolved it.
5. Stay on the branch {{branch}}. Do not push, do not switch branches, and do not change the git configuration or hooks. Leave .tikai/ alone.
6. Call submit_for_review again with a summary of the whole change, an entry in criteria_mapping for every acceptance criterion (covered, partial or missing, with a note), and your open concerns. Then stop.

## prompt
You are continuing work on the Tikai ticket {{ticket_key}}: {{ticket_title}}

An earlier session of yours implemented this ticket and submitted it for review. A human approved round {{round}}, but it does not merge: {{default_branch}} has moved since the work started, and these files conflict:
{{files}}

The earlier session could not be resumed, so this is a fresh session. What it did is still on disk: your working copy is /run/work, a git clone on the branch {{branch}}, with its commits and any uncommitted files. A copy of the ticket (description and acceptance criteria with their ids) is in .tikai/ticket.md. The current {{default_branch}} is in the git bundle {{bundle}}. The Tikai MCP tools are the source of truth for the ticket and the only way to reach the humans.

The approved plan:
{{plan}}

Questions and plans of the earlier session with the humans' answers, oldest first (these answers are final):
{{answers}}

git log of the run branch, newest first:
{{git_log}}

git status --short:
{{git_status}}

Work like this:

1. Call get_ticket to read the ticket, and call get_review_comments: the request to rebase is there as a thread, maybe with more comments.
2. Fetch the current {{default_branch}} and rebase your branch onto it:
   git fetch --quiet {{bundle}} "+refs/heads/{{default_branch}}:refs/remotes/tikai/{{default_branch}}"
   git rebase tikai/{{default_branch}}
   Resolve each conflict so that both the change on {{default_branch}} and yours keep working; do not drop either side. Then git add the files and git rebase --continue until the rebase is done. Do not merge instead of rebasing.
3. Run the checks (build, tests) and fix what the rebase broke; do not redo work that is already committed, and do not ask the questions above again. Call report_progress whenever you start a new step. When a conflict is unclear, call ask_human instead of guessing; it waits for the answer.
4. Answer every thread with reply_to_thread: what conflicted and how you resolved it.
5. Stay on the branch {{branch}}. Do not push, do not switch branches, and do not change the git configuration or hooks. Leave .tikai/ alone.
6. Call submit_for_review again with a summary of the whole change, an entry in criteria_mapping for every acceptance criterion (covered, partial or missing, with a note), and your open concerns. Then stop.

## no_plan
(none)

## no_answers
(none)

## no_commits
(no commits on the run branch yet)

## clean
(clean)

## no_files
(the hub did not list them; git rebase shows them)

## question
- You asked: {{question}}
  Answer: {{answer}}

## plan
- You proposed a plan:
{{steps}}
  Decision: {{decision}}{{notes}}
