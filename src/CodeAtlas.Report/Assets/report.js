// Burndown report viewer. The analysis already ran in C#; this script draws and filters the results.
// All names are inserted with textContent (never innerHTML), so source code can't inject markup.
(() => {
  'use strict';

  const data = expand(JSON.parse(document.getElementById('codeatlas-data').textContent));

  // ---------- appearance: color theme and light/dark mode ----------
  // The report opens with the theme it was generated with; a viewer's own choice is remembered in their
  // browser. Storage can be unavailable (private windows, blocked site data), so every access is guarded
  // and the page works the same without it. Applied before anything is drawn, since node colors come from CSS.

  const MODES = ['auto', 'light', 'dark'];
  const THEME_INFO = {
    atlas: ['Atlas', 'Balanced default'],
    nord: ['Nord', 'Cool, low-glare slate'],
    solarized: ['Solarized', 'Warm paper, deep teal'],
    contrast: ['High contrast', 'Maximum legibility'],
  };
  const storage = {
    get: key => { try { return localStorage.getItem(key); } catch { return null; } },
    set: (key, value) => { try { localStorage.setItem(key, value); } catch { /* not persisted; still applied */ } },
  };
  const themes = data.themes ?? ['atlas'];
  // Precedence: a link (report.html#theme=nord&mode=dark, for sharing a specific look), then the viewer's
  // saved choice, then the theme the report was generated with.
  const hash = new URLSearchParams(location.hash.slice(1));
  const appearance = {
    theme: [hash.get('theme'), storage.get('codeatlas:theme'), data.theme].find(t => themes.includes(t)) ?? themes[0],
    mode: [hash.get('mode'), storage.get('codeatlas:mode')].find(m => MODES.includes(m)) ?? 'auto',
  };
  const prefersDark = matchMedia('(prefers-color-scheme: dark)');
  const isDark = () => appearance.mode === 'dark' || (appearance.mode === 'auto' && prefersDark.matches);

  // Inside the Burndown desktop app (#embedded=1) the window already shows the app's name and logo.
  if (hash.get('embedded') === '1') document.documentElement.dataset.embedded = 'true';

  function applyAppearance() {
    const root = document.documentElement;
    root.dataset.palette = appearance.theme;
    if (appearance.mode === 'auto') delete root.dataset.mode;
    else root.dataset.mode = appearance.mode;
  }

  applyAppearance();

  // The C# side writes edges and coupling as compact number arrays that point into the node, kind,
  // and file tables. Turn them back into objects once, up front.
  function expand(raw) {
    const location = loc => (loc ? { filePath: raw.files[loc[0]], line: loc[1] } : null);
    const nodes = raw.nodes.map(n => ({ ...n, locations: n.locations.map(location) }));
    return {
      ...raw,
      nodes,
      edges: raw.edges.map(([source, target, kind, file, line]) => ({
        sourceId: nodes[source].id,
        targetId: nodes[target].id,
        kind: raw.kinds[kind],
        location: file === undefined ? null : location([file, line]),
      })),
      coupling: raw.coupling.map(([node, incoming, outgoing, instability]) =>
        ({ nodeId: nodes[node].id, incoming, outgoing, instability })),
    };
  }

  const ALL_KINDS = ['Inherits', 'Implements', 'ConstructorParameter', 'Field', 'Property',
    'MethodSignature', 'Instantiates', 'Calls', 'References'];
  const STRUCTURAL_KINDS = new Set(['Inherits', 'Implements']);
  const LEVELS = ['project', 'namespace', 'type'];
  const MAX_DRAWN_TYPES = 600;

  const nodesById = new Map(data.nodes.map(n => [n.id, n]));
  const couplingById = new Map(data.coupling.map(c => [c.nodeId, c]));
  const kindCounts = countBy(data.edges, e => e.kind);
  const projectCounts = countBy(data.nodes, n => n.project);
  const namespaceCycle = new Map();
  data.namespaceCycles.forEach((cycle, i) => cycle.members.forEach(ns => namespaceCycle.set(ns, i)));

  const state = {
    // Projects are the natural top level of a multi-project solution (in layered designs they *are* the layers).
    level: projectCounts.size > 1 ? 'project' : 'namespace', // 'project' | 'namespace' | 'type'
    focusProject: null,     // in namespace view, only this project's namespaces (null = all)
    focusNamespace: null,   // in type view, the namespace being explored (null = all types)
    impactOf: null,         // in type view, show only this type and what depends on it
    kinds: new Set(ALL_KINDS.filter(k => kindCounts.has(k))),
    projects: new Set(projectCounts.keys()),
  };

  // ---------- small helpers ----------

  function countBy(items, key) {
    const counts = new Map();
    for (const item of items) counts.set(key(item), (counts.get(key(item)) || 0) + 1);
    return counts;
  }

  function groupBy(items, key) {
    const groups = new Map();
    for (const item of items) {
      const k = key(item);
      if (!groups.has(k)) groups.set(k, []);
      groups.get(k).push(item);
    }
    return groups;
  }

  function el(tag, props = {}, ...children) {
    const node = document.createElement(tag);
    for (const [name, value] of Object.entries(props)) {
      if (value === undefined || value === null || value === false) continue;
      if (name === 'text') node.textContent = value;
      else if (name === 'class') node.className = value;
      else if (name.startsWith('on')) node.addEventListener(name.slice(2), value);
      else node.setAttribute(name, value === true ? '' : value);
    }
    for (const child of children.flat()) {
      if (child !== null && child !== undefined && child !== false) {
        node.append(child instanceof Node ? child : document.createTextNode(String(child)));
      }
    }
    return node;
  }

  // Unlike el(), the DOM's replaceChildren() would render null as the text "null".
  const setChildren = (parent, ...children) =>
    parent.replaceChildren(...children.flat().filter(c => c !== null && c !== undefined && c !== false));

  const nsLabel = ns => (ns === '' ? '(global namespace)' : ns);

  // Graph cards drop the solution's root namespace ("CleanArchitecture.Application.Common" shows as
  // "Application.Common"); panels keep full names. The root is the prefix shared by the namespaces under the
  // most common first segment, since a few outliers (e.g. Microsoft.Extensions.DependencyInjection for DI
  // extension classes) shouldn't stop it from applying to everything else.
  const sharedPrefix = (() => {
    const names = [...new Set(data.nodes.map(n => n.namespace))].filter(Boolean).map(ns => ns.split('.'));
    const roots = countBy(names, parts => parts[0]);
    const [root, count] = [...roots].sort((a, b) => b[1] - a[1])[0] ?? [];
    if (!root || count < 2 || count < names.length / 2) return '';
    const family = names.filter(parts => parts[0] === root);
    let length = 1;
    while (family.every(parts => parts.length > length + 1 && parts[length] === family[0][length])) length++;
    return family[0].slice(0, length).join('.') + '.';
  })();
  const shortNamespace = ns => (sharedPrefix && ns.startsWith(sharedPrefix) ? ns.slice(sharedPrefix.length) : nsLabel(ns));
  const plural = (n, word, many = `${word}s`) => `${n} ${n === 1 ? word : many}`;
  const dependencies = n => plural(n, 'dependency', 'dependencies');
  const formatLocation = loc => (loc ? `${loc.filePath}:${loc.line}` : '');
  const css = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

  const isVisible = node => state.projects.has(node.project);

  // Projects are colored from a fixed, colorblind-checked palette (validated against both themes' canvas).
  // Slots go to the largest projects in a fixed order and never change with filters, so a project keeps
  // its color everywhere. Past the palette's size, projects share a neutral "other" color rather than
  // getting generated hues that would be hard to tell apart. Red isn't in the palette: it means "cycle".
  const PROJECT_SLOTS = 7;
  const projectSlot = new Map(
    [...projectCounts].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
      .map(([project], i) => [project, i < PROJECT_SLOTS ? i + 1 : 0]));
  const projectToken = project => (projectSlot.get(project) ? `--series-${projectSlot.get(project)}` : '--series-other');
  // The canvas needs concrete colors; HTML uses the variable, so it follows theme changes on its own.
  const projectColor = project => css(projectToken(project));
  const projectColorVar = project => `var(${projectToken(project)})`;

  // Blends a color into the canvas color and returns an opaque result. Cards use this for their tint
  // instead of a translucent fill, so edges passing behind a card are hidden rather than showing through.
  function tint(color, amount) {
    const rgb = hex => [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
    const [fg, bg] = [rgb(color), rgb(css('--canvas'))];
    return `rgb(${fg.map((c, i) => Math.round(bg[i] + (c - bg[i]) * amount)).join(',')})`;
  }

  // Every click asks for the filtered edges (and often for who-depends-on-what), so compute them once per
  // filter change rather than re-scanning every edge each time. Call filtersChanged() after editing filters.
  let edgeCache = null;
  const filtersChanged = () => { edgeCache = null; };

  function visibleEdges() {
    edgeCache ??= {
      edges: data.edges.filter(e =>
        state.kinds.has(e.kind) && isVisible(nodesById.get(e.sourceId)) && isVisible(nodesById.get(e.targetId))),
    };
    return edgeCache.edges;
  }

  function incomingEdges() {
    const cache = edgeCache ?? (visibleEdges(), edgeCache);
    if (!cache.incoming) {
      cache.incoming = new Map();
      for (const e of cache.edges) {
        if (!cache.incoming.has(e.targetId)) cache.incoming.set(e.targetId, []);
        cache.incoming.get(e.targetId).push(e);
      }
    }
    return cache.incoming;
  }

  function nodeClass(node) {
    switch (node.kind) {
      case 'Interface': return 'interface';
      case 'Struct': case 'RecordStruct': case 'Record': return 'value';
      case 'Enum': return 'enum';
      default: return 'class';
    }
  }

  // Breadth-first search backwards along dependency edges: Map(typeId -> distance).
  function dependentsOf(targetId) {
    const incoming = incomingEdges();
    const distance = new Map([[targetId, 0]]);
    const queue = [targetId];
    // Read with an index: Array.shift() is O(n), which would make the search quadratic on large graphs.
    for (let next = 0; next < queue.length; next++) {
      const current = queue[next];
      for (const edge of incoming.get(current) || []) {
        if (!distance.has(edge.sourceId)) {
          distance.set(edge.sourceId, distance.get(current) + 1);
          queue.push(edge.sourceId);
        }
      }
    }
    distance.delete(targetId);
    return distance;
  }

  // ---------- graph elements ----------

  // Collapses types into groups (projects or namespaces): one node per group, one edge per pair of
  // groups weighted by how many type-level dependencies it stands for.
  function groupElements(keyOf, kind, isInCycle, types) {
    const included = new Set(types.map(n => n.id));
    const typeCounts = countBy(types, keyOf);
    const links = new Map();
    for (const e of visibleEdges()) {
      if (!included.has(e.sourceId) || !included.has(e.targetId)) continue;
      const from = keyOf(nodesById.get(e.sourceId));
      const to = keyOf(nodesById.get(e.targetId));
      if (from === to) continue;
      const key = `${from}\u0000${to}`;
      const link = links.get(key) || { from, to, count: 0 };
      link.count++;
      links.set(key, link);
    }

    // A group takes the color of the project most of its types belong to (a namespace can span projects).
    const dominantProject = new Map();
    for (const [group, members] of groupBy(types, keyOf)) {
      dominantProject.set(group, [...countBy(members, n => n.project)].sort((a, b) => b[1] - a[1])[0][0]);
    }

    const label = kind === 'namespace' ? shortNamespace : name => name;
    const nodes = [...typeCounts].map(([group, count]) => {
      const text = `${label(group)}\n${plural(count, 'type')}`;
      return {
        group: 'nodes',
        data: {
          id: `${kind}:${group}`, group, label: text, project: dominantProject.get(group),
          color: projectColor(dominantProject.get(group)), fill: tint(projectColor(dominantProject.get(group)), 0.16),
          ...cardSize(text),
        },
        classes: isInCycle(group) ? `${kind} card cycle` : `${kind} card`,
      };
    });

    const edges = [...links.values()].map(link => ({
      group: 'edges',
      data: { id: `${kind}-link:${link.from}\u0000${link.to}`, source: `${kind}:${link.from}`, target: `${kind}:${link.to}`, weight: link.count, label: String(link.count) },
      classes: isInCycle(link.from) && namespaceCycle.get(link.from) === namespaceCycle.get(link.to) ? 'cycle' : '',
    }));

    return { elements: [...nodes, ...edges], count: nodes.length };
  }

  // Projects can't reference each other in a loop (MSBuild forbids it), so only namespaces show cycles.
  const projectElements = () => groupElements(n => n.project, 'project', () => false, data.nodes.filter(isVisible));

  const namespaceElements = () => groupElements(
    n => n.namespace, 'namespace', ns => namespaceCycle.has(ns),
    data.nodes.filter(n => isVisible(n) && (state.focusProject === null || n.project === state.focusProject)));

  function typeElements() {
    const visible = data.nodes.filter(isVisible);
    const edges = visibleEdges();
    let ids;
    const context = new Set();
    let distances = null;

    if (state.impactOf) {
      distances = dependentsOf(state.impactOf);
      ids = new Set([state.impactOf, ...distances.keys()]);
    } else if (state.focusNamespace !== null) {
      ids = new Set(visible.filter(n => n.namespace === state.focusNamespace).map(n => n.id));
      // Include direct neighbours from other namespaces, drawn faded, so the focus area has context.
      for (const e of edges) {
        if (ids.has(e.sourceId) && !ids.has(e.targetId)) context.add(e.targetId);
        if (ids.has(e.targetId) && !ids.has(e.sourceId)) context.add(e.sourceId);
      }
      context.forEach(id => ids.add(id));
    } else {
      ids = new Set(visible.map(n => n.id));
    }

    if (ids.size > MAX_DRAWN_TYPES) {
      return { elements: [], count: ids.size, tooMany: true };
    }

    const nodes = [...ids].map(id => {
      const node = nodesById.get(id);
      const classes = [nodeClass(node)];
      if (context.has(id)) classes.push('context');
      if (distances) classes.push(id === state.impactOf ? 'impact-target' : `impact-${Math.min(distances.get(id), 3)}`);
      return {
        group: 'nodes',
        data: { id, label: node.name, size: 20, project: node.project, color: projectColor(node.project), distance: distances?.get(id) ?? 0 },
        classes: classes.join(' '),
      };
    });

    // Several kinds of dependency between the same two types are drawn as one edge.
    const pairs = new Map();
    for (const e of edges) {
      if (!ids.has(e.sourceId) || !ids.has(e.targetId)) continue;
      const key = `${e.sourceId}\u0000${e.targetId}`;
      if (!pairs.has(key)) pairs.set(key, { source: e.sourceId, target: e.targetId, kinds: [] });
      pairs.get(key).kinds.push(e.kind);
    }

    const links = [...pairs.values()].map(pair => ({
      group: 'edges',
      data: { id: `t:${pair.source}\u0000${pair.target}`, source: pair.source, target: pair.target, weight: 1, label: pair.kinds.join(', ') },
      classes: pair.kinds.some(k => STRUCTURAL_KINDS.has(k)) ? 'structural' : '',
    }));

    return { elements: [...nodes, ...links], count: nodes.length };
  }

  // ---------- Cytoscape ----------

  function graphStyle() {
    const font = css('--font');
    return [
      // Types: small marks colored by project, shaped by kind, labeled underneath.
      {
        selector: 'node',
        style: {
          'background-color': 'data(color)',
          'label': 'data(label)',
          'color': css('--text'),
          'font-family': font,
          'font-size': 11,
          'font-weight': 500,
          'text-valign': 'bottom',
          'text-margin-y': 6,
          'text-wrap': 'wrap',
          'text-max-width': 160,
          'width': 'data(size)',
          'height': 'data(size)',
          'border-width': 2,
          'border-color': css('--canvas'),
          'text-outline-color': css('--canvas'),
          'text-outline-width': 2,
          'transition-property': 'opacity, border-width, underlay-opacity',
          'transition-duration': 150,
          // Nodes always draw above edges, so lines pass under boxes rather than across them.
          'z-index-compare': 'manual',
          'z-index': 10,
        },
      },
      { selector: 'node.interface', style: { 'shape': 'diamond', 'width': 22, 'height': 22 } },
      { selector: 'node.value', style: { 'shape': 'round-rectangle' } },
      { selector: 'node.enum', style: { 'shape': 'hexagon' } },
      // Projects and namespaces: tinted cards with the name inside.
      {
        selector: 'node.card',
        style: {
          'shape': 'round-rectangle',
          'width': 'data(width)',
          'height': 'data(height)',
          'background-color': 'data(fill)', // opaque, so edges behind the card don't show through
          'border-color': 'data(color)',
          'border-width': 1.5,
          'text-valign': 'center',
          'text-margin-y': 0,
          'text-max-width': 400,
          'text-outline-width': 0,
          'font-size': 12,
          'line-height': 1.35,
        },
      },
      { selector: 'node.project', style: { 'font-weight': 600, 'font-size': 13, 'border-width': 2 } },
      {
        selector: 'edge',
        style: {
          'width': 'mapData(weight, 1, 25, 1, 5)',
          'line-color': css('--edge'),
          'target-arrow-color': css('--edge'),
          'target-arrow-shape': 'triangle',
          'arrow-scale': 0.8,
          'curve-style': 'bezier',
          'opacity': 0.75,
          'transition-property': 'opacity, line-color',
          'transition-duration': 150,
          'z-index-compare': 'manual',
          'z-index': 1,
        },
      },
      { selector: 'edge.structural', style: { 'line-style': 'dashed', 'line-dash-pattern': [6, 4] } },
      { selector: 'edge.cycle', style: { 'line-color': css('--danger'), 'target-arrow-color': css('--danger'), 'opacity': 1, 'width': 'mapData(weight, 1, 25, 1.5, 5)' } },
      { selector: 'node.cycle', style: { 'border-color': css('--danger'), 'border-width': 2.5 } },
      { selector: '.context', style: { 'opacity': 0.4 } },
      { selector: 'node.impact-target', style: { 'width': 34, 'height': 34, 'underlay-color': css('--accent'), 'underlay-opacity': 0.3, 'underlay-padding': 8, 'underlay-shape': 'ellipse' } },
      { selector: 'node.impact-1', style: { 'border-color': css('--danger'), 'border-width': 3 } },
      { selector: 'node.impact-2', style: { 'border-color': css('--warn'), 'border-width': 3 } },
      { selector: 'node.impact-3', style: { 'border-color': css('--muted'), 'border-width': 2 } },
      { selector: '.faded', style: { 'opacity': 0.12 } },
      { selector: 'edge.highlighted', style: {
        'line-color': css('--accent'), 'target-arrow-color': css('--accent'), 'opacity': 1, 'z-index': 2,
        'label': 'data(label)', 'font-family': font, 'font-size': 10, 'color': css('--muted'),
        'text-background-color': css('--canvas'), 'text-background-opacity': 0.9, 'text-background-padding': 2, 'text-rotation': 'autorotate',
      } },
      { selector: 'edge.highlighted.cycle', style: { 'line-color': css('--danger'), 'target-arrow-color': css('--danger') } },
      { selector: 'node.hovered', style: { 'underlay-color': css('--accent'), 'underlay-opacity': 0.12, 'underlay-padding': 6 } },
      { selector: 'node:selected', style: { 'underlay-color': css('--accent'), 'underlay-opacity': 0.28, 'underlay-padding': 7, 'border-color': css('--accent'), 'border-width': 2 } },
    ];
  }

  // Cards are sized to fit their (two-line) label.
  function cardSize(label) {
    const lines = label.split('\n');
    const longest = Math.max(...lines.map(line => line.length));
    return { width: Math.max(90, longest * 7 + 32), height: lines.length * 17 + 18 };
  }

  const cy = cytoscape({
    container: document.getElementById('cy'),
    style: graphStyle(),
    wheelSensitivity: 0.3,
    minZoom: 0.1,
    maxZoom: 3,
  });

  // A report opened in a background tab or an embedded pane can load before its canvas has a real size,
  // so the first fit is computed against the wrong area and the graph ends up off-screen. Re-fit whenever
  // the canvas changes size, until the viewer moves the view themselves (then their view is left alone).
  let viewAdjusted = false;
  cy.on('dragpan scrollzoom pinchzoom', () => { viewAdjusted = true; });
  new ResizeObserver(() => {
    cy.resize();
    if (!viewAdjusted && cy.width() > 0 && cy.height() > 0 && cy.nodes().nonempty()) fitToView(false);
  }).observe(cy.container());

  // A layered architecture diagram, used for projects and namespaces: each node's row is the length of its
  // longest dependency chain (so the innermost layer sits at the bottom and composition roots at the top),
  // and each row is ordered by the average position of its neighbors (the barycenter heuristic) to reduce
  // crossing lines. Projects can't form cycles, so their graph is a DAG; for namespaces, an edge that closes
  // a cycle is simply ignored when ranking. Rows longer than the graph is tall are wrapped.
  function layeredPositions() {
    const nodes = cy.nodes();
    const dependencies = new Map(nodes.map(n => [n.id(), n.outgoers('node').map(m => m.id())]));
    const rank = new Map();
    const rankOf = (id, visiting = new Set()) => {
      if (rank.has(id)) return rank.get(id);
      if (visiting.has(id)) return 0; // never happens for projects; guards against a malformed graph
      visiting.add(id);
      const r = Math.max(-1, ...dependencies.get(id).map(dep => rankOf(dep, visiting))) + 1;
      rank.set(id, r);
      return r;
    };
    nodes.forEach(n => rankOf(n.id()));

    const maxRank = Math.max(0, ...rank.values());
    const rows = Array.from({ length: maxRank + 1 }, () => []);
    nodes.forEach(n => rows[maxRank - rank.get(n.id())].push(n));
    rows.forEach(row => row.sort((a, b) => b.data('width') - a.data('width') || a.id().localeCompare(b.id())));

    const order = new Map();
    const reindex = () => rows.forEach(row => row.forEach((n, i) => order.set(n.id(), i - (row.length - 1) / 2)));
    reindex();
    for (let sweep = 0; sweep < 4; sweep++) {
      for (const row of rows) {
        const center = n => {
          const neighbors = n.neighborhood('node').map(m => order.get(m.id()));
          return neighbors.length ? neighbors.reduce((s, x) => s + x, 0) / neighbors.length : order.get(n.id());
        };
        row.sort((a, b) => center(a) - center(b));
      }
      reindex();
    }

    // Wrap long rows so a wide, shallow graph keeps roughly the window's shape instead of becoming a thin
    // strip. Cards sit side by side at their own widths, each sub-row centered.
    const aspect = cy.width() / Math.max(cy.height(), 1);
    const maxPerRow = Math.max(3, Math.round(Math.sqrt(nodes.length * aspect)));
    const gap = 28;
    const positions = {};
    let y = 0;
    for (const row of rows) {
      for (let start = 0; start < row.length; start += maxPerRow) {
        const chunk = row.slice(start, start + maxPerRow);
        const total = chunk.reduce((sum, n) => sum + n.data('width'), 0) + gap * (chunk.length - 1);
        let x = -total / 2;
        for (const n of chunk) {
          positions[n.id()] = { x: x + n.data('width') / 2, y };
          x += n.data('width') + gap;
        }
        y += start + maxPerRow < row.length ? 72 : 120;
      }
    }
    return positions;
  }

  let firstLayout = true;

  function runLayout() {
    // Animate small graphs when the view changes (it shows how the layout settles). The first render, and
    // big graphs, jump straight to the result so the opening view is always fitted.
    const animate = !firstLayout && cy.nodes().length <= 150;
    firstLayout = false;
    viewAdjusted = false; // a new layout gets a fresh fit
    const layout = state.impactOf
      ? { name: 'concentric', concentric: n => 10 - n.data('distance'), levelWidth: () => 1, minNodeSpacing: 48 }
      : state.level !== 'type'
        ? { name: 'preset', positions: layeredPositions() }
        : {
          name: 'cose', randomize: true, padding: 48, numIter: 2500,
          // Count labels as part of each node, so long names don't overlap.
          nodeDimensionsIncludeLabels: true, nodeRepulsion: () => 90000, idealEdgeLength: () => 160, nodeOverlap: 80,
        };
    Object.assign(layout, { animate: animate ? 'end' : false, animationDuration: 450, animationEasing: 'ease-out-cubic', fit: false });
    const running = cy.layout(layout);
    running.one('layoutstop', () => fitToView(animate));
    running.run();
  }

  // Fit everything in view, but don't blow a handful of nodes up to giant size.
  function fitToView(animate = true) {
    const padding = 40;
    const bounds = cy.elements().boundingBox();
    const container = cy.container();
    // Keep the graph clear of the legend overlay at the bottom.
    const legend = document.getElementById('legend');
    const bottom = padding + (legend.childElementCount ? legend.offsetHeight + 12 : 0);
    const width = container.clientWidth - 2 * padding;
    const height = container.clientHeight - padding - bottom;
    const fitZoom = Math.min(width / Math.max(bounds.w, 1), height / Math.max(bounds.h, 1));
    const zoom = Math.min(Math.max(fitZoom, cy.minZoom()), 1.4);
    const pan = {
      x: container.clientWidth / 2 - zoom * (bounds.x1 + bounds.w / 2),
      y: padding + height / 2 - zoom * (bounds.y1 + bounds.h / 2),
    };
    if (animate) cy.animate({ zoom, pan }, { duration: 300, easing: 'ease-out-cubic' });
    else cy.viewport({ zoom, pan });
  }

  function render() {
    const result = state.level === 'project' ? projectElements()
      : state.level === 'namespace' ? namespaceElements()
        : typeElements();
    cy.elements().remove();
    cy.add(result.elements);

    const empty = document.getElementById('empty');
    empty.hidden = result.elements.length > 0;
    empty.replaceChildren(el('p', {
      text: result.tooMany
        ? `${result.count} types is too many to draw at once. Double-click a namespace, search for a type, or narrow the filters.`
        : 'Nothing to show with the current filters.',
    }));

    // The legend first: fitting the graph leaves room for it.
    renderScope();
    renderLegend();
    if (result.elements.length > 0) runLayout();
    for (const level of LEVELS) {
      document.getElementById(`level-${level}`).setAttribute('aria-pressed', String(state.level === level));
    }
  }

  function highlight(element) {
    cy.elements().removeClass('faded highlighted');
    if (!element) return;
    const neighborhood = element.closedNeighborhood();
    cy.elements().not(neighborhood).addClass('faded');
    neighborhood.edges().addClass('highlighted');
  }

  cy.on('tap', 'node', event => {
    const target = event.target;
    highlight(target);
    if (state.level === 'project') showProjectDetails(target.data('group'));
    else if (state.level === 'namespace') showNamespaceDetails(target.data('group'));
    else showTypeDetails(target.id());
  });

  cy.on('dbltap', 'node', event => {
    if (state.level === 'project') openProject(event.target.data('group'));
    else if (state.level === 'namespace') openNamespace(event.target.data('group'));
  });

  cy.on('tap', event => {
    if (event.target === cy) {
      highlight(null);
      cy.elements().unselect();
    }
  });

  // Hovering previews a node's connections; it never overrides a click selection.
  cy.on('mouseover', 'node', event => {
    event.target.addClass('hovered');
    if (cy.$(':selected').empty()) highlight(event.target);
    cy.container().style.cursor = 'pointer';
  });

  cy.on('mouseout', 'node', event => {
    event.target.removeClass('hovered');
    if (cy.$(':selected').empty()) highlight(null);
    cy.container().style.cursor = '';
  });

  // ---------- navigation ----------

  function showLevel(level) {
    state.level = level;
    state.focusProject = null;
    state.focusNamespace = null;
    state.impactOf = null;
    render();
    showOverview();
  }

  function openProject(project) {
    state.level = 'namespace';
    state.focusProject = project;
    state.focusNamespace = null;
    state.impactOf = null;
    render();
    showProjectDetails(project);
  }

  function openNamespace(ns) {
    state.level = 'type';
    state.focusNamespace = ns;
    state.impactOf = null;
    render();
    showNamespaceDetails(ns);
  }

  function selectType(id) {
    const node = nodesById.get(id);
    if (!node) return;
    if (!state.projects.has(node.project)) {
      state.projects.add(node.project);
      filtersChanged();
    }
    state.level = 'type';
    state.focusNamespace = node.namespace;
    state.impactOf = null;
    render();
    renderFilters();
    focusElement(id);
    showTypeDetails(id);
    selectTab('details');
  }

  function showImpact(id) {
    state.level = 'type';
    state.impactOf = id;
    render();
    focusElement(id);
    showTypeDetails(id);
  }

  function focusElement(id) {
    const element = cy.getElementById(id);
    if (element.nonempty()) {
      cy.elements().unselect();
      element.select();
      highlight(element);
    }
  }

  function renderScope() {
    const scope = document.getElementById('scope');
    const topLevel = projectCounts.size > 1 ? 'project' : 'namespace';
    const back = el('button', {
      type: 'button', class: 'link', text: topLevel === 'project' ? '← All projects' : '← All namespaces',
      onclick: () => showLevel(topLevel),
    });

    if (state.impactOf) {
      const node = nodesById.get(state.impactOf);
      scope.replaceChildren(el('span', {}, 'Impact of ', el('strong', { text: node.name })), back);
    } else if (state.level === 'type' && state.focusNamespace !== null) {
      scope.replaceChildren(el('span', {}, 'Types in ', el('strong', { text: nsLabel(state.focusNamespace) })), back);
    } else if (state.level === 'type') {
      scope.replaceChildren(el('span', { text: 'All types' }));
    } else if (state.level === 'namespace' && state.focusProject !== null) {
      scope.replaceChildren(el('span', {}, 'Namespaces in ', el('strong', { text: state.focusProject })), back);
    } else if (state.level === 'project') {
      scope.replaceChildren(el('span', { text: 'Double-click a project to see its namespaces.' }));
    } else {
      scope.replaceChildren(el('span', { text: 'Double-click a namespace to see its types.' }));
    }
  }

  function renderLegend() {
    const item = (mark, label) => el('span', { class: 'legend-item' }, mark, label);
    const swatch = (color, shape = 'square') => el('i', { class: `swatch ${shape}`, style: `--swatch:${color}` });
    const line = (color, dashed) => el('i', { class: `line${dashed ? ' dashed' : ''}`, style: `--swatch:${color}` });

    // Which projects are on screen right now, in palette order, with "Other" last.
    const onScreen = new Set(cy.nodes().map(n => n.data('project')));
    const colored = [...projectSlot].filter(([p, slot]) => slot > 0 && onScreen.has(p)).sort((a, b) => a[1] - b[1]);
    const projects = colored.map(([p]) => item(swatch(projectColorVar(p)), p));
    if ([...onScreen].some(p => p && !projectSlot.get(p))) projects.push(item(swatch('var(--series-other)'), 'Other projects'));

    const rows = [];
    if (state.impactOf) {
      rows.push([
        item(swatch(css('--accent'), 'round'), 'Changed type'),
        item(swatch(css('--danger'), 'ring'), 'Direct dependent'),
        item(swatch(css('--warn'), 'ring'), '2 steps away'),
        item(swatch(css('--muted'), 'ring'), '3+ steps'),
      ]);
    } else {
      // At the project level every card is labeled with its project, so a color key would only repeat it.
      if (state.level !== 'project') rows.push(projects);
      const marks = state.level === 'type'
        ? [item(swatch(css('--muted'), 'round'), 'Class'), item(swatch(css('--muted'), 'diamond'), 'Interface'),
           item(swatch(css('--muted')), 'Record / struct'), item(line(css('--edge'), true), 'Inherits / implements')]
        : [item(line(css('--edge')), 'Depends on (thicker = more)')];
      if (state.level === 'namespace') marks.push(item(line(css('--danger')), 'Cycle'));
      rows.push(marks);
    }

    document.getElementById('legend').replaceChildren(
      ...rows.filter(r => r.length).map(r => el('div', { class: 'legend-row' }, r)));
  }

  // ---------- details panel ----------

  const detailsPanel = document.getElementById('tab-details');

  function showOverview() {
    const namespaces = new Set(data.nodes.map(n => n.namespace)).size;
    setChildren(detailsPanel, 
      el('h2', { text: 'Overview' }),
      el('p', { class: 'subtle', text: `Generated ${data.generatedAt}${data.toolVersion ? ` by Burndown ${data.toolVersion}` : ''}.` }),
      el('div', { class: 'metrics' },
        metric(data.nodes.length, 'types'), metric(namespaces, 'namespaces'), metric(data.edges.length, 'dependencies')),
      el('h3', { text: 'How to use' }),
      el('ul', { class: 'subtle' },
        el('li', { text: 'Click a node to see its dependencies; double-click a project or namespace to drill in.' }),
        el('li', { text: 'Search for a type, then use "Show impact" to see everything that depends on it.' }),
        el('li', { text: 'The Health tab lists cycles, rule violations, and the most connected types.' })),
    );
  }

  function metric(value, label) {
    return el('div', { class: 'metric' }, el('b', { text: String(value) }), el('span', { text: label }));
  }

  function typeRow(id, meta) {
    const node = nodesById.get(id);
    return el('li', {}, el('button', { type: 'button', class: 'row', onclick: () => selectType(id) },
      el('span', { class: 'name', text: node.fullName }), meta ? el('span', { class: 'meta', text: meta }) : null));
  }

  function groupedEdges(edges, otherEnd) {
    const byType = new Map();
    for (const e of edges) {
      const other = otherEnd(e);
      if (!byType.has(other)) byType.set(other, []);
      byType.get(other).push(e.kind);
    }
    return [...byType].sort((a, b) => nodesById.get(a[0]).fullName.localeCompare(nodesById.get(b[0]).fullName));
  }

  function showTypeDetails(id) {
    const node = nodesById.get(id);
    const coupling = couplingById.get(id);
    const edges = visibleEdges();
    const uses = groupedEdges(edges.filter(e => e.sourceId === id), e => e.targetId);
    const usedBy = groupedEdges(edges.filter(e => e.targetId === id), e => e.sourceId);
    const impact = state.impactOf === id ? dependentsOf(id) : null;

    setChildren(detailsPanel, 
      el('h2', { text: node.fullName }),
      el('div', { class: 'chips' },
        el('span', { class: 'chip', text: node.kind }),
        el('span', { class: 'chip' }, el('i', { class: 'dot', style: `--swatch:${projectColorVar(node.project)}` }), node.project),
        namespaceCycle.has(node.namespace) ? el('span', { class: 'chip danger', text: 'Namespace in a cycle' }) : null),
      ...node.locations.map(loc => el('div', { class: 'location', text: formatLocation(loc) })),
      coupling ? el('div', { class: 'metrics' },
        metric(coupling.incoming, 'used by'), metric(coupling.outgoing, 'uses'), metric(coupling.instability.toFixed(2), 'instability')) : null,
      el('div', { class: 'actions' },
        state.impactOf === id
          ? el('button', { type: 'button', class: 'button', text: 'Back to namespace', onclick: () => openNamespace(node.namespace) })
          : el('button', { type: 'button', class: 'button primary', text: 'Show impact', onclick: () => showImpact(id) }),
        state.focusNamespace !== node.namespace || state.impactOf
          ? el('button', { type: 'button', class: 'button', text: 'Open namespace', onclick: () => openNamespace(node.namespace) })
          : null),
      impact ? impactSection(impact) : null,
      el('h3', { text: `Depends on (${uses.length})` }),
      uses.length ? el('ul', { class: 'list' }, uses.map(([other, kinds]) => typeRow(other, kinds.join(', ')))) : el('p', { class: 'subtle', text: 'Nothing in the solution.' }),
      el('h3', { text: `Used by (${usedBy.length})` }),
      usedBy.length ? el('ul', { class: 'list' }, usedBy.map(([other, kinds]) => typeRow(other, kinds.join(', ')))) : el('p', { class: 'subtle', text: 'Nothing in the solution.' }),
    );
    selectTab('details');
  }

  function impactSection(distances) {
    if (distances.size === 0) {
      return el('p', { class: 'subtle', text: 'Nothing in the solution depends on this type.' });
    }
    const byDistance = new Map();
    for (const [id, distance] of distances) {
      if (!byDistance.has(distance)) byDistance.set(distance, []);
      byDistance.get(distance).push(id);
    }
    return el('div', {},
      el('h3', { text: `Potentially affected (${distances.size})` }),
      ...[...byDistance].sort((a, b) => a[0] - b[0]).map(([distance, ids]) => el('div', {},
        el('p', { class: 'subtle', text: distance === 1 ? 'Direct dependents' : `${distance} steps away` }),
        el('ul', { class: 'list' }, ids.sort().map(other => typeRow(other))))),
    );
  }

  function showNamespaceDetails(ns) {
    const types = data.nodes.filter(n => n.namespace === ns && isVisible(n)).sort((a, b) => a.name.localeCompare(b.name));
    const outgoing = new Map();
    const incoming = new Map();
    for (const e of visibleEdges()) {
      const from = nodesById.get(e.sourceId).namespace;
      const to = nodesById.get(e.targetId).namespace;
      if (from === to) continue;
      if (from === ns) outgoing.set(to, (outgoing.get(to) || 0) + 1);
      if (to === ns) incoming.set(from, (incoming.get(from) || 0) + 1);
    }
    const nsRows = map => el('ul', { class: 'list' }, [...map].sort((a, b) => b[1] - a[1]).map(([other, count]) =>
      el('li', {}, el('button', { type: 'button', class: 'row', onclick: () => openNamespace(other) },
        el('span', { class: 'name', text: nsLabel(other) }), el('span', { class: 'meta', text: dependencies(count) + (namespaceCycle.has(other) && namespaceCycle.get(other) === namespaceCycle.get(ns) ? ' · cycle' : '') })))));

    setChildren(detailsPanel, 
      el('h2', { text: nsLabel(ns) }),
      el('div', { class: 'chips' },
        el('span', { class: 'chip', text: plural(types.length, 'type') }),
        namespaceCycle.has(ns) ? el('span', { class: 'chip danger', text: 'Part of a cycle' }) : null),
      state.focusNamespace === ns ? null : el('div', { class: 'actions' },
        el('button', { type: 'button', class: 'button primary', text: 'Show types', onclick: () => openNamespace(ns) })),
      el('h3', { text: `Depends on (${outgoing.size})` }),
      outgoing.size ? nsRows(outgoing) : el('p', { class: 'subtle', text: 'No other namespaces.' }),
      el('h3', { text: `Used by (${incoming.size})` }),
      incoming.size ? nsRows(incoming) : el('p', { class: 'subtle', text: 'No other namespaces.' }),
      el('h3', { text: 'Types' }),
      el('ul', { class: 'list' }, types.map(t => typeRow(t.id, t.kind))),
    );
    selectTab('details');
  }

  function showProjectDetails(project) {
    const types = data.nodes.filter(n => n.project === project);
    const namespaces = countBy(types, n => n.namespace);
    const outgoing = new Map();
    const incoming = new Map();
    for (const e of visibleEdges()) {
      const from = nodesById.get(e.sourceId).project;
      const to = nodesById.get(e.targetId).project;
      if (from === to) continue;
      if (from === project) outgoing.set(to, (outgoing.get(to) || 0) + 1);
      if (to === project) incoming.set(from, (incoming.get(from) || 0) + 1);
    }
    const projectRows = map => el('ul', { class: 'list' }, [...map].sort((a, b) => b[1] - a[1]).map(([other, count]) =>
      el('li', {}, el('button', { type: 'button', class: 'row', onclick: () => openProject(other) },
        el('span', { class: 'name', text: other }), el('span', { class: 'meta', text: dependencies(count) })))));

    setChildren(detailsPanel,
      el('h2', { text: project }),
      el('div', { class: 'chips' },
        el('span', { class: 'chip', text: 'Project' }),
        el('span', { class: 'chip', text: plural(types.length, 'type') }),
        el('span', { class: 'chip', text: plural(namespaces.size, 'namespace') })),
      state.level === 'namespace' && state.focusProject === project ? null : el('div', { class: 'actions' },
        el('button', { type: 'button', class: 'button primary', text: 'Show namespaces', onclick: () => openProject(project) })),
      el('h3', { text: `Depends on (${outgoing.size})` }),
      outgoing.size ? projectRows(outgoing) : el('p', { class: 'subtle', text: 'No other projects.' }),
      el('h3', { text: `Used by (${incoming.size})` }),
      incoming.size ? projectRows(incoming) : el('p', { class: 'subtle', text: 'No other projects.' }),
      el('h3', { text: 'Namespaces' }),
      el('ul', { class: 'list' }, [...namespaces].sort((a, b) => a[0].localeCompare(b[0])).map(([ns, count]) =>
        el('li', {}, el('button', { type: 'button', class: 'row', onclick: () => openNamespace(ns) },
          el('span', { class: 'name', text: nsLabel(ns) }), el('span', { class: 'meta', text: plural(count, 'type') }))))),
    );
    selectTab('details');
  }

  // ---------- health panel ----------

  function renderHealth() {
    const panel = document.getElementById('tab-health');
    const check = data.check;
    const sections = [];

    if (check) {
      const byRule = new Map(check.rules.map(r => [r.id, []]));
      check.violations.forEach(v => byRule.get(v.ruleId)?.push(v));
      sections.push(
        el('h3', { text: 'Architecture rules' }),
        el('div', { class: 'chips' },
          el('span', { class: `chip ${check.errorCount ? 'danger' : 'ok'}`, text: plural(check.errorCount, 'error') }),
          el('span', { class: `chip ${check.warningCount ? 'warn' : ''}`, text: plural(check.warningCount, 'warning') })),
        ...check.rules.map(rule => {
          const violations = byRule.get(rule.id);
          const status = violations.length === 0 ? 'pass' : rule.severity === 'Error' ? 'error' : 'warning';
          return el('div', { class: `finding ${status}` },
            el('div', { class: 'rule-title' },
              el('span', { text: rule.name }),
              el('span', { class: `chip ${status === 'pass' ? 'ok' : status === 'error' ? 'danger' : 'warn'}`, text: status === 'pass' ? 'pass' : plural(violations.length, 'violation') })),
            el('p', { class: 'subtle', text: rule.description }),
            violations.length ? el('ul', { class: 'list' }, violations.map(v => el('li', {},
              el('button', { type: 'button', class: 'row', onclick: () => v.nodeId && selectType(v.nodeId) },
                el('span', {}, el('span', { class: 'name', text: v.message }), v.location ? el('span', { class: 'location', text: ` ${formatLocation(v.location)}` }) : null))))) : null);
        }),
        ...check.warnings.map(w => el('p', { class: 'subtle', text: `Config warning: ${w}` })),
      );
    }

    sections.push(el('h3', { text: `Namespace cycles (${data.namespaceCycles.length})` }));
    if (data.namespaceCycles.length === 0) {
      sections.push(el('p', { class: 'subtle', text: 'None. Namespaces depend on each other in one direction only.' }));
    }
    data.namespaceCycles.forEach(cycle => sections.push(
      el('div', { class: 'finding error' },
        el('div', { class: 'rule-title', text: plural(cycle.members.length, 'namespace') }),
        el('p', { text: cycle.examplePath.map(nsLabel).join(' → ') }),
        el('p', { class: 'subtle', text: `${dependencies(cycle.evidenceCount)} involved.` }),
        el('div', { class: 'actions' }, el('button', {
          type: 'button', class: 'button', text: 'Show in graph',
          onclick: () => { showLevel('namespace'); highlight(cy.nodes().filter(n => cycle.members.includes(n.data('group')))); },
        }))),
    ));

    const top = (key, label) => {
      const ranked = data.coupling.filter(c => c[key] > 0).sort((a, b) => b[key] - a[key]).slice(0, 8);
      return [el('h3', { text: label }), ranked.length
        ? el('ul', { class: 'list' }, ranked.map(c => typeRow(c.nodeId, String(c[key]))))
        : el('p', { class: 'subtle', text: 'No dependencies.' })];
    };
    sections.push(...top('incoming', 'Most depended on'), ...top('outgoing', 'Most dependencies'));

    if (data.warnings?.length) {
      sections.push(el('h3', { text: 'Analysis warnings' }), ...data.warnings.map(w => el('p', { class: 'subtle', text: w })));
    }

    setChildren(panel, ...sections);

    const problems = (check?.errorCount || 0) + data.namespaceCycles.length;
    const badge = document.getElementById('health-badge');
    badge.hidden = problems === 0;
    badge.textContent = String(problems);
  }

  // ---------- filters panel ----------

  function renderFilters() {
    const panel = document.getElementById('tab-filters');

    const checkbox = (set, value, label, count, color) => el('label', { class: 'checkbox' },
      el('input', {
        type: 'checkbox', checked: set.has(value),
        onchange: event => { event.target.checked ? set.add(value) : set.delete(value); filtersChanged(); render(); renderHealth(); },
      }),
      color ? el('i', { class: 'swatch', style: `background:${color}` }) : null,
      el('span', { text: label }),
      el('span', { class: 'count', text: String(count) }));

    setChildren(panel, 
      el('h3', { text: 'Dependency kinds' }),
      el('p', { class: 'subtle', text: 'Hide noisy kinds (like References) to see the structure.' }),
      ...ALL_KINDS.filter(k => kindCounts.has(k)).map(k => checkbox(state.kinds, k, k, kindCounts.get(k))),
      el('h3', { text: 'Projects' }),
      ...[...projectCounts].sort((a, b) => a[0].localeCompare(b[0])).map(([p, count]) => checkbox(state.projects, p, p, count, projectColorVar(p))),
    );
  }

  // ---------- search ----------

  const searchInput = document.getElementById('search');
  const searchResults = document.getElementById('search-results');

  searchInput.addEventListener('input', () => {
    const query = searchInput.value.trim().toLowerCase();
    if (!query) {
      searchResults.hidden = true;
      return;
    }
    const matches = data.nodes
      .filter(n => n.fullName.toLowerCase().includes(query))
      .sort((a, b) => (a.name.toLowerCase().startsWith(query) ? 0 : 1) - (b.name.toLowerCase().startsWith(query) ? 0 : 1) || a.name.localeCompare(b.name))
      .slice(0, 12);

    searchResults.replaceChildren(...(matches.length
      ? matches.map(n => el('li', {}, el('button', {
        type: 'button',
        onclick: () => { searchInput.value = ''; searchResults.hidden = true; selectType(n.id); },
      }, el('span', { text: n.name }), el('small', { text: `${nsLabel(n.namespace)} · ${n.project}` }))))
      : [el('li', { class: 'subtle', text: 'No matching types.' })]));
    searchResults.hidden = false;
  });

  searchInput.addEventListener('keydown', event => {
    if (event.key === 'Enter') searchResults.querySelector('button')?.click();
    if (event.key === 'Escape') { searchInput.value = ''; searchResults.hidden = true; }
  });

  // "/" jumps to search, as on GitHub and most docs sites.
  document.addEventListener('keydown', event => {
    if (event.key === '/' && !event.target.closest('input, textarea')) {
      event.preventDefault();
      searchInput.focus();
    }
  });

  document.addEventListener('click', event => {
    if (!event.target.closest('.controls')) searchResults.hidden = true;
  });

  // ---------- tabs, toolbar, theme ----------

  function selectTab(name) {
    for (const tab of document.querySelectorAll('.tabs [role="tab"]')) {
      const selected = tab.dataset.tab === name;
      tab.setAttribute('aria-selected', String(selected));
      document.getElementById(`tab-${tab.dataset.tab}`).hidden = !selected;
    }
  }

  document.querySelectorAll('.tabs [role="tab"]').forEach(tab => tab.addEventListener('click', () => selectTab(tab.dataset.tab)));
  document.getElementById('level-project').addEventListener('click', () => showLevel('project'));
  document.getElementById('level-namespace').addEventListener('click', () => showLevel('namespace'));
  document.getElementById('level-type').addEventListener('click', () => showLevel('type'));
  // Zoom toward a target that accumulates across clicks, so quick repeated clicks all count instead of each
  // restarting the animation from wherever the previous one had reached.
  let zoomTarget = null;
  const zoomBy = factor => {
    viewAdjusted = true;
    zoomTarget = Math.min(Math.max((zoomTarget ?? cy.zoom()) * factor, cy.minZoom()), cy.maxZoom());
    cy.stop();
    cy.animate(
      { zoom: { level: zoomTarget, renderedPosition: { x: cy.width() / 2, y: cy.height() / 2 } } },
      { duration: 150, complete: () => { zoomTarget = null; } });
  };
  document.getElementById('zoom-in').addEventListener('click', () => zoomBy(1.25));
  document.getElementById('zoom-out').addEventListener('click', () => zoomBy(0.8));
  document.getElementById('fit').addEventListener('click', () => {
    viewAdjusted = false;
    fitToView();
  });
  document.getElementById('relayout').addEventListener('click', runLayout);

  // ---------- theme picker ----------

  // HTML follows the CSS variables by itself, but the canvas was painted with concrete colors, so repaint
  // it in place: same positions and zoom, new palette.
  function refreshColors() {
    cy.batch(() => cy.nodes().forEach(n => {
      const color = projectColor(n.data('project'));
      n.data('color', color);
      if (n.hasClass('card')) n.data('fill', tint(color, 0.16));
    }));
    cy.style(graphStyle());
    renderLegend();
  }

  const themeButton = document.getElementById('theme-button');
  const themePopover = document.getElementById('theme-popover');

  function renderThemePicker() {
    const options = themes.map(name => {
      const [label, description] = THEME_INFO[name] ?? [name, ''];
      const swatches = el('span', { class: 'theme-swatches', 'aria-hidden': 'true' });
      const option = el('button', {
        type: 'button', role: 'radio', class: 'theme-option', 'data-palette': name,
        'aria-checked': String(name === appearance.theme),
        onclick: () => setAppearance({ theme: name }),
      }, el('span', {}, el('strong', { text: label }), el('small', { text: description })), swatches);
      return { option, swatches, name };
    });
    document.getElementById('theme-options').replaceChildren(...options.map(o => o.option));

    // Each option carries its theme's data-palette, so its own computed style holds that theme's colors.
    const prefix = isDark() ? '--d-series-' : '--l-series-';
    for (const { option, swatches } of options) {
      const style = getComputedStyle(option);
      swatches.replaceChildren(...[1, 2, 3, 4, 5].map(i =>
        el('i', { style: `--swatch:${style.getPropertyValue(prefix + i).trim()}` })));
    }

    for (const button of document.querySelectorAll('#mode-options [data-mode]')) {
      button.setAttribute('aria-pressed', String(button.dataset.mode === appearance.mode));
    }
  }

  function setAppearance(change) {
    Object.assign(appearance, change);
    storage.set('codeatlas:theme', appearance.theme);
    storage.set('codeatlas:mode', appearance.mode);
    applyAppearance();
    refreshColors();
    renderThemePicker();
  }

  const closeThemePicker = () => {
    themePopover.hidden = true;
    themeButton.setAttribute('aria-expanded', 'false');
  };

  themeButton.addEventListener('click', event => {
    event.stopPropagation();
    themePopover.hidden = !themePopover.hidden;
    themeButton.setAttribute('aria-expanded', String(!themePopover.hidden));
    if (!themePopover.hidden) renderThemePicker();
  });
  document.querySelectorAll('#mode-options [data-mode]').forEach(button =>
    button.addEventListener('click', () => setAppearance({ mode: button.dataset.mode })));
  // Close on clicks outside the menu. Picking a theme re-renders the options, which detaches the clicked
  // button before this runs, so check the path recorded at dispatch rather than the (now detached) target.
  document.addEventListener('click', event => {
    if (!themePopover.hidden && !event.composedPath().includes(themePopover.parentElement)) closeThemePicker();
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && !themePopover.hidden) {
      closeThemePicker();
      themeButton.focus();
    }
  });

  // In Auto mode the page follows the operating system's light/dark setting live.
  prefersDark.addEventListener('change', () => {
    if (appearance.mode !== 'auto') return;
    refreshColors();
    if (!themePopover.hidden) renderThemePicker();
  });

  // ---------- start ----------

  document.getElementById('title').textContent = data.title;
  const stats = [[data.nodes.length, 'types'], [data.edges.length, 'dependencies'], [projectCounts.size, 'projects']];
  document.getElementById('stats').replaceChildren(...stats.map(([value, label]) =>
    el('div', {}, el('dt', { text: label }), el('dd', { text: String(value) }))));

  // A link like report.html#level=namespace opens on that level (it can be combined with theme and mode).
  const requestedLevel = hash.get('level');
  if (LEVELS.includes(requestedLevel)) state.level = requestedLevel;

  render();
  showOverview();
  renderHealth();
  renderFilters();
})();
