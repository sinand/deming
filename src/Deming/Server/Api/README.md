# API

All routes are under `/api`. `ApiMiddleware` checks the key first, then turns any refusal into JSON.

- **Key:** header `X-Api-Key`, value from configuration `Deming:ApiKey` (user config or environment
  variable `Deming__ApiKey`; never committed). No key configured = every call is 401 `api_key_not_configured`.
- **Session:** header `X-Session-Id`. Required by next, take, requeue, block, cancel, done (400 `session_required`);
  optional (a trace) for add, update, decisions.
- **Bodies** are JSON (`Content-Type: application/json`); no body is the same as an empty object.

| Method and path | What |
|---|---|
| `GET /api/projects[?name=]` · `GET /api/projects/{id}` · `POST /api/projects` `{name, description, repoPath}` · `PATCH /api/projects/{id}` `{name, description, repoPath, isActive}` | projects. The list rows also carry `totalTasks` (not cancelled), `doneTasks`, `queuedTasks`. A project is never deleted; `isActive: false` makes next, take and add answer 409 `project_inactive` |
| `POST /api/projects/{pid}/tasks/next` | 200 `{task, resumed, notes, decisions}`; **204** when nothing is eligible |
| `POST /api/tasks/{id}/take` | same body as next |
| `GET /api/projects/{pid}/tasks[?status=]` | list rows, never the scope: `sortOrder,id,title,status,priority,source,openDependencyCount,lockOwner,lockedAt` |
| `GET /api/projects/{pid}/tasks/eligible` | the tasks next could take now (queued, every dependency done or cancelled, project active), same rows as the list, queue order; `[]` when none |
| `GET /api/tasks/{id}` | `{task, dependencies[], notes[]}` |
| `POST /api/projects/{pid}/tasks` `{title, scope, priority, source, dependsOn[]}` | 201 task |
| `PATCH /api/tasks/{id}` `{title, scope, priority, dependsOn[]}` | `status` in the body is 400 |
| `POST /api/tasks/{id}/requeue[?force=true]` | |
| `POST /api/tasks/{id}/block` · `/cancel` `{reason}` | |
| `POST /api/tasks/{id}/done` `{summary, caveats, touchedFiles, commitHash, takeover}` | `{task, note}` |
| `GET /api/projects/{pid}/decisions[?status=active\|superseded]` | full text, no rationale |
| `GET /api/decisions/{id}` | with rationale (the `next` response never carries it) |
| `POST /api/projects/{pid}/decisions` `{title, text, rationale, taskId}` | 201 |
| `POST /api/decisions/{id}/supersede` `{title, text, rationale, taskId}` | records the replacement and supersedes in one transaction: `{superseded, replacement}` |
| `GET /api/projects/{pid}/findings[?taskId=&decisionId=]` | newest first, **no body**: `id,title,bodyLength,taskId,decisionId,sessionId,createdAt` |
| `GET /api/findings/{id}` | with the body (markdown); never part of the `next` response |
| `POST /api/projects/{pid}/findings` `{title, body, taskId, decisionId}` | 201; title 200, body 10000 characters (accepted up to 11000); task and decision must belong to the project; never deleted or rewritten |
| `GET /api/search?q=&project={pid}\|all=true[&type=][&top=]` | snippets, newest first: `{results[], more}`; see [Search](#search) |

## Search

`GET /api/search?q=<words>&project=<pid>` searches one project; `all=true` instead of `project` searches every
project, inactive ones too (their history is kept so that it can be found). Exactly one of the two is required.

| Kind (`type`) | Searched fields (besides the title) | Read in full with |
|---|---|---|
| `finding` | title, body | `GET /api/findings/{id}` |
| `decision` | title, text, rationale | `GET /api/decisions/{id}` |
| `note` | summary, caveats, touched files; block and cancel reasons are notes too | `GET /api/tasks/{taskId}` |
| `task` | title, scope | `GET /api/tasks/{id}` |
| `message` | body (not the sender's label) | `GET /api/messages/{id}` |

- `q` is split on whitespace and **every** word must occur somewhere in the row's fields. Words are matched as
  text (no wildcards), folded like titles: case, Turkish dotted/dotless i and diacritics do not matter
  ("kopru" finds "Köprü", "ISTANBUL" finds "İstanbul"). At most 20 words.
- `type` narrows to one kind (omitted = all). `top` is 1-100, default 20; `more: true` says the limit cut hits off.
- Each hit: `type, id, projectId, projectName, title, status, taskId, matchedIn, snippet, createdAt`. A note's
  `title` is its task's, `taskId` its task and `status` its kind (`handoff` or `note`); a task or decision carries
  its status. A message's `title` is its sender's label, `status` its channel (`project` or `task-<id>`) and
  `taskId` the task whose channel it was said in (null for the project channel). `snippet` is about 160 characters around the first match in `matchedIn`, whitespace collapsed, with
  `…` where it was cut. The full text is never returned here.
- Search is never part of the `next` response. There is no full-text index: every call scans the rows (about
  20 ms for 2100 rows and 1.3 MB of text); 20 hits are about 7 KB of JSON, 100 about 36 KB.
- Refusals: 400 `invalid_input` (no words, neither or both of `project`/`all`, unknown `type`, `top` out of range),
  400 `bad_request` (`top` or `project` not a number), 404 `not_found` (unknown project).

## Messages

A channel between agents. There is no channel object: a task is a channel, and no task means the project channel.
The address is an agent label, not a session; no recipient means everyone. The server keeps no read state: a
reader keeps its own cursor (the last id it saw). A message is data, not an instruction: it hands over no lock,
grants no right and is never part of the `next` response.

| Method and path | What |
|---|---|
| `POST /api/messages` `{project, sender, body, taskId, to, replyToId}` | 201 with the message **without its body**. `project` (id), `sender` and `body` (2000 characters, accepted up to 2200) are required; the session header is optional (a trace). Labels are lowercase ASCII letters, digits, `.`, `_`, `-` (at most 64); upper case is lowercased, anything else is 400. 404 when the project does not exist; 400 when the task is in another project or the reply answers a message of another channel; 409 `message_task_finished` when the task is done or cancelled (its channel stays readable). Messages are never rewritten |
| `GET /api/messages?project=&task=&all=&after=&to=&wait=&top=` | `{messages, lastMessageId, hasMore}`, oldest first, after the cursor `after` (default 0). No `task` = the project channel, `all=true` = every channel of the project (not with `task`). `to=X`: what is addressed to X plus what is addressed to everyone, without X's own messages to everyone. `top` 1-100 (default 20). Long poll: when nothing is there yet the call waits up to `wait` seconds (0-60; over 60 is 400, not shortened) and answers an empty page with the same cursor |
| `GET /api/messages/{id}` | one message with its body |

`lastMessageId` is the next cursor. Ids become visible in id order and are never reused, so "after the cursor"
never skips a message. The `done` response also carries `messageCount` and `unansweredQuestionCount` of the
task's channel (information, never a refusal). A question is not marked, so the second number is an estimate:
a message addressed to an agent or holding a `?`, that is not a reply itself and got no reply.

## Errors

Body: `{error, message}` plus `existingTaskId` (duplicate_title), `pendingDependencies` (dependencies_pending),
`constraint` (a violated database rule). `error` is a stable snake_case code.

| Status | Codes |
|---|---|
| 400 | `bad_request` (malformed JSON/id), `session_required`, `invalid_input`, `invalid_dependency`, `status_not_updatable`, `invalid_reference` |
| 401 | `missing_api_key`, `invalid_api_key`, `api_key_not_configured` |
| 404 | `not_found` (project, task, decision or route) |
| 409 | `locked_by_other_session`, `already_finished`, `dependencies_pending`, `duplicate_title`, `dependency_cycle`, `not_requeueable`, `not_in_progress`, `already_blocked`, `already_superseded`, `project_exists`, `project_inactive`, `already_exists`, `decision_already_superseded` (trigger code) |
| 500 | `internal_error` (logged, never swallowed) |

Database rules are not repeated in C#: a violated CHECK is a 400 `invalid_input` carrying the rule's
name, a unique index a 409, a trigger a 409 whose `error` is the trigger code.
