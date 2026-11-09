# Data layer

SQLite (WAL) through `Microsoft.Data.Sqlite`. No ORM. The database does the
work — CHECK, partial UNIQUE, triggers, `UPDATE … RETURNING`, recursive CTEs; C# opens the
transaction and calls named queries.

## Layout

```
Data/
  Schema/NNNN_name.sql        schema scripts, applied once in order (schema_version table)
  Sql/<Area>/<Operation>.sql  named queries, called as "<Area>.<Operation>"
  Models/<Model>.cs           one class per row shape, properties = columns
  Services/<Area>Service.cs   one service per area, registered in DataServiceCollectionExtensions
```

Both `.sql` folders are embedded by wildcard in `Deming.csproj`; a new file needs no csproj edit.

## Adding a query

1. Write `Data/Sql/<Area>/<Operation>.sql`. Area and Operation are PascalCase, one folder level
   (`Task/Lock.sql` → `Task.Lock`). A misnamed file stops the process at first use.
2. Start the file with a comment: name, what it does, its parameters.
3. Parameters are `@PascalCase`, matching model property names, so `AddParameters(model)` works.
4. Normalized text is computed in SQL: `deming_normalize(@Title)`.
5. Time: the schema default is `strftime('%Y-%m-%dT%H:%M:%fZ','now')`. Queries whose result
   depends on "now" (lock timeout) take `@Now` from C# (`TimeProvider`) so tests can move time.
6. `Every_embedded_query_compiles_against_the_schema` prepares every query against a fresh
   database; a typo fails the test run.

## Calling

```csharp
// one statement, own connection
var p = await db.Sql("Project.GetByName").AddParameter("Name", name).GetModelAsync<Project>(ct);

// several statements atomically: BEGIN IMMEDIATE, commit on return, rollback on throw
var task = await db.InTransactionAsync(async tx =>
{
    var t = await tx.Sql("Task.Lock").AddParameters(args).GetModelAsync<TaskItem>(ct);
    await tx.Sql("Task.Touch").AddParameter("Id", t!.Id).ExecuteAsync(ct);
    return t;
}, ct);
```

`GetModelAsync<T>` (first row or null), `GetModelListAsync<T>`, `GetScalarAsync<T>` (first
column), `ExecuteAsync` (rows changed). Mapping: `snake_case` column ↔ `PascalCase` property,
`[Column("x")]` to rename, `[NotMapped]` to skip. Timestamps are UTC `DateTime`.

## Lock flow (TaskService)

`NextAsync(projectId, sessionId)` / `TakeAsync(taskId, sessionId)`: one BEGIN IMMEDIATE each;
first step releases expired locks (`Task.ReleaseExpiredLocks`, timeout `Deming:Locks:TimeoutMinutes`,
default 120, clock = `TimeProvider`). Refusals are `TaskFlowException` (`Error`, `HttpStatus`,
`PendingDependencies`). `TaskLockResult` carries the `Notes` and `Decisions` handed out with the task.

## Lifecycle (TaskLifecycleService)

`AddAsync`, `UpdateAsync`, `RequeueAsync`, `BlockAsync`, `CancelAsync`, `DoneAsync`: one BEGIN IMMEDIATE
each, refusals are `TaskFlowException`. These do **not** release expired locks (that stays
in next/take); an expired lock (`locked_at < now − TimeoutMinutes`) is simply treated as free.
`DuplicateTitle` carries `ExistingTaskId`. `update` takes `UpdateTaskRequest` (null = unchanged,
empty scope clears, non-null `DependsOn` replaces the set, any `Status` = 400). block/cancel write
the reason as a `note`; done writes a `handoff` note (`TakenOverFrom` when another/expired owner).

## Errors

A violated rule throws `ConstraintViolationException` with `Query`, `Kind` and `Constraint`:

| Kind | Constraint holds |
|---|---|
| Check | constraint name, e.g. `ck_task_lock_only_in_progress` |
| Unique | SQLite's text, e.g. `task.project_id, task.title_normalized` |
| Trigger | the `RAISE(ABORT, 'code')` code, e.g. `dependency_cycle` |

Services translate these to API errors (`ex.Is(ConstraintKind.Trigger, "dependency_cycle")`).
Nothing is swallowed; a missing parameter is an exception, not a NULL.

## Changing the schema

Never edit an applied script. Add `Data/Schema/000N_what.sql` (next number, no gaps); `serve`
applies it at startup in a transaction. A database newer than the binary is refused.
