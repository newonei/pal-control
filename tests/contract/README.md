# Contract tests

Planned checks:

- validate `control-api.yaml` as OpenAPI 3.1;
- validate bridge hello/command/result fixtures with `message.schema.json`;
- ensure generated TypeScript and C# clients stay compatible;
- verify every mutating endpoint requires Idempotency-Key, reason and revision;
- prevent newly added Pal fields from exposing owner/instance/raw save mutation.
