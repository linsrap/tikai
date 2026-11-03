## message
A human reviewed round {{round}} of your work and requested changes. Their comments are waiting for you in Tikai.

Work like this:

1. Call get_review_comments to read the open review threads. Each has a thread_id, the file and lines it is about (in the round you submitted), and the comments sent to you.
2. Fix what the comments ask for. Call report_progress whenever you start a new step. When a comment is unclear, call ask_human instead of guessing; it waits for the answer.
3. Answer every thread with reply_to_thread: what you changed, or why you did not change it.
4. Commit your work with git on the branch {{branch}}, in small commits with clear messages. Do not push, do not switch branches, and do not change the git configuration or hooks. Leave .tikai/ alone.
5. Call submit_for_review again with a summary of the whole change, an entry in criteria_mapping for every acceptance criterion (covered, partial or missing, with a note), and your open concerns. Then stop.

## prompt
You are continuing work on the Tikai ticket {{ticket_key}}: {{ticket_title}}

An earlier session of yours implemented this ticket and submitted it for review. A human reviewed round {{round}} and requested changes; their comments are waiting for you in Tikai. The earlier session could not be resumed, so this is a fresh session. What it did is still on disk: your working copy is /run/work, a git clone on the branch {{branch}}, with its commits and any uncommitted files. A copy of the ticket (description and acceptance criteria with their ids) is in .tikai/ticket.md. The Tikai MCP tools are the source of truth for the ticket and the only way to reach the humans.

The approved plan:
{{plan}}

Questions and plans of the earlier session with the humans' answers, oldest first (these answers are final):
{{answers}}

git log of the run branch, newest first:
{{git_log}}

git status --short:
{{git_status}}

Work like this:

1. Call get_ticket to read the ticket, and call get_review_comments to read the open review threads. Each has a thread_id, the file and lines it is about (in the round that was reviewed), and the comments sent to you.
2. Fix what the comments ask for; do not redo work that is already committed, and do not ask the questions above again. Call report_progress whenever you start a new step. When a comment is unclear, call ask_human instead of guessing; it waits for the answer.
3. Answer every thread with reply_to_thread: what you changed, or why you did not change it. A thread that already has your reply needs another one only if you changed something more.
4. Commit your work with git on the branch {{branch}}, in small commits with clear messages. Do not push, do not switch branches, and do not change the git configuration or hooks. Leave .tikai/ alone.
5. Call submit_for_review again with a summary of the whole change, an entry in criteria_mapping for every acceptance criterion (covered, partial or missing, with a note), and your open concerns. Then stop.

## no_plan
(none)

## no_answers
(none)

## no_commits
(no commits on the run branch yet)

## clean
(clean)

## question
- You asked: {{question}}
  Answer: {{answer}}

## plan
- You proposed a plan:
{{steps}}
  Decision: {{decision}}{{notes}}
