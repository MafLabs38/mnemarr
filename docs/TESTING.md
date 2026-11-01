# Mnemarr — Testing strategy

> Same stack, conventions and gates as Portarr (ADR 0008), reduced to what Mnemarr has today. The pyramid is the same; levels are added with the features that need them.

## 1. Pyramid

```text
                 ▲   E2E (Playwright)                       ~10 %   few, user journeys, on the real image
              ▲▲▲▲   Integration (API in memory)           ~20 %   the real host, a real data folder
         ▲▲▲▲▲▲▲▲▲   Unit (backend, frontend, collector)    ~70 %   fast, isolated, all business logic
```

## 2. Tools

| Level | Backend (.NET) | Frontend | Collector (Rust) |
|---|---|---|---|
| Unit | xUnit v3, NSubstitute, AwesomeAssertions, `FakeTimeProvider` | Vitest, Testing Library | `cargo test` (unit tests next to the code) |
| Integration | `WebApplicationFactory<Program>` over the real host (`Category=Integration`) | — | `tests/` running the real binary |
| E2E | — | Playwright (Chromium and Firefox) with `@axe-core/playwright`, against the image | — |
| Mutation | Stryker.NET on `Mnemarr.Core` | StrykerJS | — |

Later levels (Testcontainers for external services, a recorded fixture of the assistant CLI's protocol for the runner) arrive with the verticals that need them.

## 3. Conventions

- Backend test classes are named `ClassUnderTestFixture`, methods `should_<result>_when_<condition>`. Integration tests carry `[Trait("Category", "Integration")]`.
- No real `Thread.Sleep` or delay: use `FakeTimeProvider`. No test depends on the order of another or on the network.
- Tests never contain real infrastructure values or real memory content.
- A test protects a behavior someone relies on; it does not assert a framework or a getter. The `test-reviewer` agent challenges this on every pull request.
- E2E selectors use roles and visible text, not styling.

## 4. CI gates

| Rule | Threshold |
|---|---|
| Core (.NET) coverage | at least 70 % overall, **at least 80 % on new code**, enforced by the SonarQube quality gate. That job only runs once a SonarQube server is configured, so **no check is made required in the repository rules before it is**: until then the backend threshold is not enforced by CI |
| Frontend coverage | at least 70 %, enforced by Vitest (`coverage.thresholds`): the test job fails below it |
| Unit tests | 100 % green, under 3 minutes |
| Integration | 100 % green |
| E2E smoke | 100 % green on every pull request, with no accessibility violation |
| Collector | `cargo fmt --check`, `cargo clippy -D warnings`, tests, on Linux and Windows |
| Security | CodeQL (no new high or critical alert), gitleaks, Trivy |
| Mutation | Reported weekly, not enforced: a surviving mutant in code a feature relies on is a missing test |

## 5. Running the tests

See [CONTRIBUTING.md](../CONTRIBUTING.md#development-setup) for the commands, including the container-based way for a small machine.
