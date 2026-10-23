# CodeAtlas

A command-line tool that maps the dependencies inside a C# solution using the Roslyn compiler APIs, then
finds dependency cycles, answers "what could this change affect?", enforces architecture rules in CI, and
writes an interactive HTML report you can explore in any browser.

CodeAtlas loads a `.sln`, `.slnx`, or `.csproj`, finds every type the solution declares, and records how those
types depend on each other, from inheritance and constructor injection down to individual method calls.

## Usage

```bash
# Analyze once and save the graph; the other commands can reuse it without reloading the solution
dotnet run --project src/CodeAtlas.Cli -- analyze samples/ShopSample/ShopSample.sln -o graph.json

dotnet run --project src/CodeAtlas.Cli -- cycles graph.json
dotnet run --project src/CodeAtlas.Cli -- impact graph.json IOrderRepository
dotnet run --project src/CodeAtlas.Cli -- check samples/ShopSample/ShopSample.sln --sarif results.sarif
dotnet run --project src/CodeAtlas.Cli -- report samples/ShopSample/ShopSample.sln -o report.html
```

`samples/ShopSample` is a small three-project solution with deliberate design problems, so the commands have
something to find:

```
$ codeatlas cycles graph.json

Found 1 namespace cycle(s).

Cycle 1: 2 namespaces
  Shop.Domain.Billing
  Shop.Domain.Orders
  Loop: Shop.Domain.Billing -> Shop.Domain.Orders -> Shop.Domain.Billing
  Caused by:
    Shop.Domain.Billing.Invoice -[Property]-> Shop.Domain.Orders.Order  Shop.Domain/Billing/Invoice.cs:8
    Shop.Domain.Orders.Order -[Calls]-> Shop.Domain.Billing.TaxCalculator  Shop.Domain/Orders/Order.cs:12
    ...
```

```
$ codeatlas impact graph.json IOrderRepository

Changing Shop.Domain.Orders.IOrderRepository may affect 2 type(s) in: Shop.App (2)

Direct dependents
  Shop.App.Infrastructure.InMemoryOrderRepository  Shop.App/Infrastructure/InMemoryOrderRepository.cs:5
    via InMemoryOrderRepository -[Implements]-> IOrderRepository
  Shop.App.OrderService  Shop.App/OrderService.cs:12
    via OrderService -[ConstructorParameter]-> IOrderRepository
```

| Command | Options |
| --- | --- |
| `analyze <solution>` | `-o <file>` save the graph as JSON |
| `cycles <input>` | `--level namespace` (default) or `--level type` |
| `impact <input> <type>` | `--depth <n>` limit the steps; `--kinds Calls,Implements` follow only some dependency kinds |
| `check <input>` | `--config <file>` (default: `codeatlas.json` next to the input); `--sarif <file>`; `--path-prefix <dir>` |
| `report <input>` | `-o <file>` (default `codeatlas-report.html`); `--config <file>` to include rule results; `--title <text>` |

`<input>` is a solution or a saved `graph.json`. `<type>` can be a full name (`Shop.Domain.Orders.Order`), a
simple name (`Order`), or a namespace suffix (`Orders.Order`). If the name is ambiguous, CodeAtlas lists the matches.

Exit codes: `0` success, `1` bad arguments or config, `2` analysis failed, `3` architecture rules failed.

## Architecture rules

`codeatlas check` enforces rules written in a `codeatlas.json` file, so a layering mistake fails the build
instead of being found in code review months later:

```jsonc
{
  // Layers give names to groups of namespaces.
  "layers": {
    "Domain": ["Shop.Domain.**"],
    "Application": ["Shop.App.**"],
    "Api": ["Shop.Api.**"]
  },
  "rules": [
    { "name": "Domain has no outward dependencies", "from": "Domain", "mayOnlyDependOn": [] },
    { "name": "Application does not depend on the API", "from": "Application", "forbid": ["Api"] },
    { "name": "Services use repositories through interfaces", "from": "Shop.App", "forbid": ["Shop.App.Infrastructure.**"] },
    { "name": "Billing does not depend on Orders", "from": "Shop.Domain.Billing", "forbid": ["Shop.Domain.Orders"], "severity": "warning" }
  ],
  "namespaceCycles": "error"
}
```

```
$ codeatlas check samples/ShopSample/ShopSample.sln

[PASS] Domain has no outward dependencies
[PASS] Application does not depend on the API
[FAIL] Services use repositories through interfaces (1 violation(s))
   Shop.App.OrderService depends on Shop.App.Infrastructure.InMemoryOrderRepository (Instantiates).
     at Shop.App/OrderService.cs:15
[WARN] Billing does not depend on Orders (1 violation(s))
   ...
[FAIL] No namespace cycles (1 violation(s))
   ...
Check failed: 2 error(s), 1 warning(s).
```

| Setting | Meaning |
| --- | --- |
| `from`, `forbid`, `mayOnlyDependOn` | A layer name, a namespace pattern, or `project:Name` |
| `forbid` | Types in `from` must not depend on any of these |
| `mayOnlyDependOn` | Types in `from` may depend only on these and on `from` itself; `[]` means "only itself" |
| `kinds` | Optional: only check some dependency kinds, e.g. `["Calls", "Instantiates"]` |
| `severity` | `error` (default, fails the check) or `warning` |
| `id` | Optional stable id for SARIF; defaults to a slug of the name |
| `namespaceCycles` | `error` or `warning` to report every namespace cycle; omit to skip |

Namespace patterns: `Shop.Domain` matches exactly; `*` matches within one segment (`Shop.*.Api`); a trailing
`.**` matches the namespace and everything under it.

The config is checked before the (slow) solution load. Misspelled settings are errors rather than being
silently ignored, and a pattern that matches no types is reported, since a typo would otherwise make a rule pass forever.

### SARIF and GitHub code scanning

`--sarif <file>` writes [SARIF 2.1.0](https://docs.oasis-open.org/sarif/sarif/v2.1.0/sarif-v2.1.0.html), the
format GitHub code scanning reads, so violations show up as annotations on pull requests. SARIF paths must be
relative to the repository root. When the input is a solution inside a Git repository, CodeAtlas works out the
prefix itself (e.g. `samples/ShopSample/`); for a saved `graph.json`, pass `--path-prefix`.

To show violations in code scanning, add this after the `check` step in a workflow
(requires `permissions: security-events: write`):

```yaml
- uses: github/codeql-action/upload-sarif@v3
  if: always()
  with:
    sarif_file: results.sarif
```

`.github/workflows/ci.yml` builds and tests CodeAtlas, then runs `check` on the sample and fails unless it
finds the sample's deliberate violations (exit code 3).

## Interactive report

`codeatlas report` writes one self-contained HTML file. The page, its script, Cytoscape.js, and the graph data are
all inlined, so it opens offline, with no server, and can be attached to a CI run or emailed.

- **Namespace map** (the default): namespaces sized by type count, with edges weighted by how many
  dependencies they carry. Namespace cycles are drawn in red. Drawing hundreds of types at once is unreadable,
  so the report starts here and lets you drill in.
- **Type view**: double-click a namespace to see its types, plus faded neighbors from other namespaces for context.
  Shapes and colors show the kind of type; dashed edges are inheritance or interface implementation.
- **Details**: click any type for its source locations, coupling (used by, uses, instability), and what it depends
  on and what depends on it, with the kinds of each dependency.
- **Impact**: "Show impact" redraws the graph as rings around the type: direct dependents, then 2 steps away, and so on.
- **Health**: rule results from `codeatlas.json` (when present), namespace cycles, and the most connected types.
- **Filters**: hide noisy dependency kinds (like `References`) or whole projects; search finds any type by name.

The analysis (cycles, coupling, rule results) runs in C#. The page's script only draws and filters. Every name is
inserted as text rather than HTML, and the embedded JSON escapes `<`, so a type name can't inject markup into
the page (there's a test with a hostile name). The page supports light and dark mode and works at phone width.

## How it works

### Extracting the graph

| Step | Approach | Why |
| --- | --- | --- |
| Load | `MSBuildWorkspace` opens the solution with real project context (references, compiler options) | Semantic analysis needs to know what every name refers to, which reading `.cs` files alone can't provide |
| Find types | Walk namespace **symbols** (`Compilation.Assembly.GlobalNamespace`), not syntax trees | A `partial` class is one symbol across many files, so it becomes one node automatically |
| Identify types | `{assembly}:{documentation comment id}`, e.g. `Shop.Domain:Shop.Data.Repository`1` | Survives across compilations, keeps generic arity, and separates same-named types |
| Declarations | Base type, interfaces, constructor parameters, fields, properties, method signatures | Generic arguments and arrays are unwrapped: `Dictionary<string, List<Order>>` depends on `Order` |
| Bodies | Every name, `new`, and call in method bodies, accessors, lambdas, and initializers is bound with the semantic model | `var x = Load()` resolves to the real return type, and `order.ToDto()` is attributed to the extension class that declares it, not to `Order` |
| Filter | Edges are kept only when the target is a solution type | Framework and NuGet types would otherwise drown the graph |

Each edge records its **kind** (`Inherits`, `Implements`, `ConstructorParameter`, `Field`, `Property`,
`MethodSignature`, `Instantiates`, `Calls`, `References`) and the source line it was first seen on.

### Cycle detection

Cycles are found with **Tarjan's strongly connected components algorithm** in O(V + E). The textbook version is
recursive with depth equal to the longest dependency chain, which can overflow the stack on large graphs, so
`StronglyConnectedComponents` keeps an explicit stack instead (a test runs it on a 200,000-node chain).

Cycles are reported at the **namespace** level by default. Type-level cycles are often fine (an `Order` and its
`OrderLine` pointing at each other), but namespaces that depend on each other usually mean two layers have become
one. Each cycle comes with a shortest example loop (found by breadth-first search) and the type-level edges that cause it.

### Change impact

`ImpactAnalyzer` walks dependency edges **backwards** from the target with a breadth-first search. BFS reaches
types in order of distance, so the first path found to each one is a shortest path, and a visited set keeps
cycles from looping forever. Results are *potentially* affected types: whether a change breaks a dependent depends
on what changed.

### What the analysis won't guess

- **Unresolved types** (usually an unrestored solution) are reported as warnings, and no edges are drawn through them.
- **Project load failures** from MSBuild are reported instead of silently producing a partial graph.
- **Injected source** from outside the solution folder (e.g. the test SDK's `AutoGeneratedProgram`) is excluded.

### Known limitations

Static analysis only sees what the source code says. In the sample, `OrdersController` depends on `IOrderService`,
and only the dependency-injection setup at runtime connects that to `OrderService`. So `impact IOrderRepository`
correctly stops at `OrderService` instead of reaching the controller. Reflection and `dynamic` are invisible for the same reason.

> **Security note:** loading a solution evaluates its MSBuild files, which can run arbitrary build logic.
> Only analyze solutions you trust.

## Project layout

```
src/
  CodeAtlas.Core/        Graph model, JSON, graph algorithms (cycles, impact, coupling), and architecture
                         rules + SARIF; no Roslyn dependency
  CodeAtlas.Analysis/    Roslyn extraction: SolutionAnalyzer -> GraphBuilder -> Dependency/BodyDependencyExtractor
  CodeAtlas.Report/      HTML report writer; Assets/ holds the page, styles, viewer script, and vendored Cytoscape.js
  CodeAtlas.Cli/         The `codeatlas` command
tests/
  CodeAtlas.Analysis.Tests/   Extraction tests that compile C# source strings in memory (no MSBuild needed)
  CodeAtlas.Core.Tests/       Algorithm and rules tests on small hand-built graphs
  CodeAtlas.Report.Tests/     Report tests: self-contained output, embedded data, escaping of hostile names
samples/
  ShopSample/            A small solution with deliberate rule violations and a codeatlas.json, for demos and CI
```

## Development

```bash
dotnet build
dotnet test
```

The target framework is set once in `Directory.Build.props`.

## Roadmap

- [x] **v0.1** Load solutions, extract types and declaration-level dependencies, export JSON
- [x] **v0.2** Method-body dependencies, namespace and type cycle detection (iterative Tarjan), change-impact analysis
- [x] **v0.3** `codeatlas check`: architecture rules from a config file, non-zero exit code, SARIF output, GitHub Actions CI
- [x] **v0.4** Self-contained interactive HTML report (Cytoscape.js) with coupling metrics
- [ ] **Later** Pull-request impact (`--since main`), analysis history across commits, retarget to .NET 10 LTS

## Third-party code

The HTML report inlines [Cytoscape.js](https://js.cytoscape.org/) 3.34.3 (MIT License, Copyright (c) 2016-2026
The Cytoscape Consortium); see `src/CodeAtlas.Report/Assets/cytoscape.LICENSE`.
