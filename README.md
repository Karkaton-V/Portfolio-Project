# CodeAtlas

A command-line tool that maps the dependencies inside a C# solution using the Roslyn compiler APIs, then
finds dependency cycles and answers "what could this change affect?"

CodeAtlas loads a `.sln`, `.slnx`, or `.csproj`, finds every type the solution declares, and records how those
types depend on each other, from inheritance and constructor injection down to individual method calls.

## Usage

```bash
# Analyze once and save the graph; the other commands can reuse it without reloading the solution
dotnet run --project src/CodeAtlas.Cli -- analyze samples/ShopSample/ShopSample.sln -o graph.json

dotnet run --project src/CodeAtlas.Cli -- cycles graph.json
dotnet run --project src/CodeAtlas.Cli -- impact graph.json IOrderRepository
```

`samples/ShopSample` is a small three-project solution with a deliberate design problem, so the commands have
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
  Shop.App.InMemoryOrderRepository  Shop.App/OrderService.cs:21
    via InMemoryOrderRepository -[Implements]-> IOrderRepository
  Shop.App.OrderService  Shop.App/OrderService.cs:11
    via OrderService -[ConstructorParameter]-> IOrderRepository
```

| Command | Options |
| --- | --- |
| `analyze <solution>` | `-o <file>` save the graph as JSON |
| `cycles <input>` | `--level namespace` (default) or `--level type` |
| `impact <input> <type>` | `--depth <n>` limit the steps; `--kinds Calls,Implements` follow only some dependency kinds |

`<input>` is a solution or a saved `graph.json`. `<type>` can be a full name (`Shop.Domain.Orders.Order`), a
simple name (`Order`), or a namespace suffix (`Orders.Order`). If the name is ambiguous, CodeAtlas lists the matches.

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
  CodeAtlas.Core/        Graph model, JSON, and graph algorithms (cycles, impact); no Roslyn dependency
  CodeAtlas.Analysis/    Roslyn extraction: SolutionAnalyzer -> GraphBuilder -> Dependency/BodyDependencyExtractor
  CodeAtlas.Cli/         The `codeatlas` command
tests/
  CodeAtlas.Analysis.Tests/   Extraction tests that compile C# source strings in memory (no MSBuild needed)
  CodeAtlas.Core.Tests/       Algorithm tests on small hand-built graphs
samples/
  ShopSample/            A small solution with a deliberate namespace cycle, for demos
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
- [ ] **v0.3** `codeatlas check`: architecture rules from a config file, non-zero exit code, SARIF output for GitHub code scanning
- [ ] **v0.4** Self-contained interactive HTML report (Cytoscape.js)
- [ ] **Later** Coupling metrics, pull-request impact (`--since main`), analysis history across commits, retarget to .NET 10 LTS
