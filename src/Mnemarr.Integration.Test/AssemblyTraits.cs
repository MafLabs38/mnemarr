// Every test of this assembly starts the real host: CI runs the unit pass with `--filter-not-trait Category=Integration`,
// so the trait is set once here and a new class cannot forget it.
[assembly: Xunit.Trait("Category", "Integration")]
