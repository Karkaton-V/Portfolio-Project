// CodeAtlas report viewer. The analysis already ran in C#; this script draws and filters the results.
// All names are inserted with textContent (never innerHTML), so source code can't inject markup.
(() => {
  'use strict';

  const data = JSON.parse(document.getElementById('codeatlas-data').textContent);

  const ALL_KINDS = ['Inherits', 'Implements', 'ConstructorParameter', 'Field', 'Property',
    'MethodSignature', 'Instantiates', 'Calls', 'References'];
  const STRUCTURAL_KINDS = new Set(['Inherits', 'Implements']);
  const MAX_DRAWN_TYPES = 600;

  const nodesById = new Map(data.nodes.map(n => [n.id, n]));
  const couplingById = new Map(data.coupling.map(c => [c.nodeId, c]));
  const kindCounts = countBy(data.edges, e => e.kind);
  const projectCounts = countBy(data.nodes, n => n.project);
  const namespaceCycle = new Map();
  data.namespaceCycles.forEach((cycle, i) => cycle.members.forEach(ns => namespaceCycle.set(ns, i)));

  const state = {
    level: 'namespace',     // 'namespace' | 'type'
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
  const plural = (n, word, many = `${word}s`) => `${n} ${n === 1 ? word : many}`;
  const dependencies = n => plural(n, 'dependency', 'dependencies');
  const formatLocation = loc => (loc ? `${loc.filePath}:${loc.line}` : '');
  const css = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

  const isVisible = node => state.projects.has(node.project);

  function visibleEdges() {
    return data.edges.filter(e =>
      state.kinds.has(e.kind) && isVisible(nodesById.get(e.sourceId)) && isVisible(nodesById.get(e.targetId)));
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
    const incoming = new Map();
    for (const e of visibleEdges()) {
      if (!incoming.has(e.targetId)) incoming.set(e.targetId, []);
      incoming.get(e.targetId).push(e.sourceId);
    }

    const distance = new Map([[targetId, 0]]);
    const queue = [targetId];
    while (queue.length > 0) {
      const current = queue.shift();
      for (const dependent of incoming.get(current) || []) {
        if (!distance.has(dependent)) {
          distance.set(dependent, distance.get(current) + 1);
          queue.push(dependent);
        }
      }
    }
    distance.delete(targetId);
    return distance;
  }

  // ---------- graph elements ----------

  function namespaceElements() {
    const typeCounts = countBy(data.nodes.filter(isVisible), n => n.namespace);
    const links = new Map();
    for (const e of visibleEdges()) {
      const from = nodesById.get(e.sourceId).namespace;
      const to = nodesById.get(e.targetId).namespace;
      if (from === to) continue;
      const key = `${from}\u0000${to}`;
      const link = links.get(key) || { from, to, count: 0 };
      link.count++;
      links.set(key, link);
    }

    const nodes = [...typeCounts].map(([ns, count]) => ({
      group: 'nodes',
      data: { id: `ns:${ns}`, ns, label: `${nsLabel(ns)}\n${plural(count, 'type')}`, size: 28 + Math.sqrt(count) * 9 },
      classes: namespaceCycle.has(ns) ? 'namespace cycle' : 'namespace',
    }));

    const edges = [...links.values()].map(link => {
      const inCycle = namespaceCycle.has(link.from) && namespaceCycle.get(link.from) === namespaceCycle.get(link.to);
      return {
        group: 'edges',
        data: { id: `nsl:${link.from}\u0000${link.to}`, source: `ns:${link.from}`, target: `ns:${link.to}`, weight: link.count, label: String(link.count) },
        classes: inCycle ? 'cycle' : '',
      };
    });

    return { elements: [...nodes, ...edges], count: nodes.length };
  }

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
      return { group: 'nodes', data: { id, label: node.name, size: 26, distance: distances?.get(id) ?? 0 }, classes: classes.join(' ') };
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
    return [
      {
        selector: 'node',
        style: {
          'background-color': css('--node-class'),
          'label': 'data(label)',
          'color': css('--text'),
          'font-family': css('--font'),
          'font-size': 11,
          'text-valign': 'bottom',
          'text-margin-y': 5,
          'text-wrap': 'wrap',
          'text-max-width': 160,
          'text-background-color': css('--label-bg'),
          'text-background-opacity': 1,
          'text-background-padding': 2,
          'text-background-shape': 'roundrectangle',
          'width': 'data(size)',
          'height': 'data(size)',
          'border-width': 2,
          'border-color': css('--surface'),
        },
      },
      { selector: 'node.namespace', style: { 'background-color': css('--node-namespace'), 'shape': 'round-rectangle' } },
      { selector: 'node.interface', style: { 'background-color': css('--node-interface'), 'shape': 'diamond' } },
      { selector: 'node.value', style: { 'background-color': css('--node-value'), 'shape': 'round-rectangle' } },
      { selector: 'node.enum', style: { 'background-color': css('--node-enum'), 'shape': 'hexagon' } },
      {
        selector: 'edge',
        style: {
          'width': 'mapData(weight, 1, 25, 1.2, 6)',
          'line-color': css('--edge'),
          'target-arrow-color': css('--edge'),
          'target-arrow-shape': 'triangle',
          'arrow-scale': 0.9,
          'curve-style': 'bezier',
          'opacity': 0.85,
        },
      },
      { selector: 'edge.structural', style: { 'line-style': 'dashed', 'line-color': css('--edge-structural'), 'target-arrow-color': css('--edge-structural') } },
      { selector: 'edge.cycle', style: { 'line-color': css('--danger'), 'target-arrow-color': css('--danger'), 'opacity': 1 } },
      { selector: 'node.cycle', style: { 'border-color': css('--danger'), 'border-width': 3 } },
      { selector: '.context', style: { 'opacity': 0.45 } },
      { selector: 'node.impact-target', style: { 'background-color': css('--accent'), 'width': 38, 'height': 38 } },
      { selector: 'node.impact-1', style: { 'border-color': css('--danger'), 'border-width': 4 } },
      { selector: 'node.impact-2', style: { 'border-color': css('--warn'), 'border-width': 4 } },
      { selector: 'node.impact-3', style: { 'border-color': css('--edge'), 'border-width': 3 } },
      { selector: '.faded', style: { 'opacity': 0.12 } },
      { selector: 'edge.highlighted', style: { 'label': 'data(label)', 'font-size': 10, 'color': css('--muted'), 'text-background-color': css('--label-bg'), 'text-background-opacity': 1, 'text-rotation': 'autorotate' } },
      { selector: 'node:selected', style: { 'border-color': css('--accent'), 'border-width': 4 } },
    ];
  }

  const cy = cytoscape({
    container: document.getElementById('cy'),
    style: graphStyle(),
    wheelSensitivity: 0.3,
    minZoom: 0.1,
    maxZoom: 3,
  });

  function runLayout() {
    const layout = state.impactOf
      ? { name: 'concentric', concentric: n => 10 - n.data('distance'), levelWidth: () => 1, minNodeSpacing: 40, animate: false }
      : {
        name: 'cose', animate: false, randomize: true, padding: 40,
        // Count labels as part of each node, so long namespace names don't overlap.
        nodeDimensionsIncludeLabels: true, nodeRepulsion: () => 60000, idealEdgeLength: () => 140, nodeOverlap: 40,
      };
    cy.layout(layout).run();
    fitToView();
  }

  // Fit everything in view, but don't blow a handful of nodes up to giant size.
  function fitToView() {
    cy.fit(undefined, 40);
    if (cy.zoom() > 1.4) {
      cy.zoom(1.4);
      cy.center();
    }
  }

  function render() {
    const result = state.level === 'namespace' ? namespaceElements() : typeElements();
    cy.elements().remove();
    cy.add(result.elements);

    const empty = document.getElementById('empty');
    empty.hidden = result.elements.length > 0;
    empty.replaceChildren(el('p', {
      text: result.tooMany
        ? `${result.count} types is too many to draw at once. Double-click a namespace, search for a type, or narrow the filters.`
        : 'Nothing to show with the current filters.',
    }));

    if (result.elements.length > 0) runLayout();
    renderScope();
    renderLegend();
    document.getElementById('level-namespace').setAttribute('aria-pressed', String(state.level === 'namespace'));
    document.getElementById('level-type').setAttribute('aria-pressed', String(state.level === 'type'));
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
    if (state.level === 'namespace') showNamespaceDetails(target.data('ns'));
    else showTypeDetails(target.id());
  });

  cy.on('dbltap', 'node', event => {
    if (state.level === 'namespace') openNamespace(event.target.data('ns'));
  });

  cy.on('tap', event => {
    if (event.target === cy) {
      highlight(null);
      cy.elements().unselect();
    }
  });

  // ---------- navigation ----------

  function showLevel(level) {
    state.level = level;
    state.focusNamespace = null;
    state.impactOf = null;
    render();
    showOverview();
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
    state.projects.add(node.project);
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
    const back = el('button', { type: 'button', class: 'link', text: '← All namespaces', onclick: () => showLevel('namespace') });

    if (state.impactOf) {
      const node = nodesById.get(state.impactOf);
      scope.replaceChildren(el('span', {}, 'Impact of ', el('strong', { text: node.name })), back);
    } else if (state.level === 'type' && state.focusNamespace !== null) {
      scope.replaceChildren(el('span', {}, 'Types in ', el('strong', { text: nsLabel(state.focusNamespace) })), back);
    } else if (state.level === 'type') {
      scope.replaceChildren(el('span', { text: 'All types' }));
    } else {
      scope.replaceChildren(el('span', { text: 'Double-click a namespace to see its types.' }));
    }
  }

  function renderLegend() {
    const swatch = (color, label, shape = '3px') =>
      el('span', {}, el('i', { class: 'swatch', style: `background:${css(color)};border-radius:${shape}` }), label);
    const line = (color, label, dashed) =>
      el('span', {}, el('i', { class: 'swatch', style: `height:0;border-top:2px ${dashed ? 'dashed' : 'solid'} ${css(color)};width:16px;border-radius:0` }), label);

    const items = state.level === 'namespace'
      ? [swatch('--node-namespace', 'Namespace'), line('--edge', 'Depends on (count)'), line('--danger', 'Cycle')]
      : state.impactOf
        ? [swatch('--accent', 'Changed type', '50%'), swatch('--danger', 'Direct dependent', '50%'), swatch('--warn', '2 steps away', '50%'), swatch('--edge', '3+ steps', '50%')]
        : [swatch('--node-class', 'Class', '50%'), swatch('--node-interface', 'Interface'), swatch('--node-value', 'Record / struct'),
           swatch('--node-enum', 'Enum'), line('--edge-structural', 'Inherits / implements', true)];
    document.getElementById('legend').replaceChildren(...items);
  }

  // ---------- details panel ----------

  const detailsPanel = document.getElementById('tab-details');

  function showOverview() {
    const namespaces = new Set(data.nodes.map(n => n.namespace)).size;
    setChildren(detailsPanel, 
      el('h2', { text: 'Overview' }),
      el('p', { class: 'subtle', text: `Generated ${data.generatedAt}${data.toolVersion ? ` by CodeAtlas ${data.toolVersion}` : ''}.` }),
      el('div', { class: 'metrics' },
        metric(data.nodes.length, 'types'), metric(namespaces, 'namespaces'), metric(data.edges.length, 'dependencies')),
      el('h3', { text: 'How to use' }),
      el('ul', { class: 'subtle' },
        el('li', { text: 'Click a node to see its dependencies; double-click a namespace to open it.' }),
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
        el('span', { class: 'chip', text: node.project }),
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
          onclick: () => { showLevel('namespace'); highlight(cy.nodes().filter(n => cycle.members.includes(n.data('ns')))); },
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
    const kindColor = kind => (STRUCTURAL_KINDS.has(kind) ? css('--edge-structural') : css('--edge'));

    const checkbox = (set, value, label, count, color) => el('label', { class: 'checkbox' },
      el('input', {
        type: 'checkbox', checked: set.has(value),
        onchange: event => { event.target.checked ? set.add(value) : set.delete(value); render(); renderHealth(); },
      }),
      color ? el('i', { class: 'swatch', style: `background:${color}` }) : null,
      el('span', { text: label }),
      el('span', { class: 'count', text: String(count) }));

    setChildren(panel, 
      el('h3', { text: 'Dependency kinds' }),
      el('p', { class: 'subtle', text: 'Hide noisy kinds (like References) to see the structure.' }),
      ...ALL_KINDS.filter(k => kindCounts.has(k)).map(k => checkbox(state.kinds, k, k, kindCounts.get(k), kindColor(k))),
      el('h3', { text: 'Projects' }),
      ...[...projectCounts].sort((a, b) => a[0].localeCompare(b[0])).map(([p, count]) => checkbox(state.projects, p, p, count)),
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
  document.getElementById('level-namespace').addEventListener('click', () => showLevel('namespace'));
  document.getElementById('level-type').addEventListener('click', () => showLevel('type'));
  document.getElementById('fit').addEventListener('click', fitToView);
  document.getElementById('relayout').addEventListener('click', runLayout);

  matchMedia('(prefers-color-scheme: dark)').addEventListener('change', () => {
    cy.style(graphStyle());
    renderLegend();
    renderFilters();
  });

  // ---------- start ----------

  document.getElementById('title').textContent = data.title;
  const stats = [[data.nodes.length, 'types'], [data.edges.length, 'dependencies'], [projectCounts.size, 'projects']];
  document.getElementById('stats').replaceChildren(...stats.map(([value, label]) =>
    el('div', {}, el('dt', { text: label }), el('dd', { text: String(value) }))));

  render();
  showOverview();
  renderHealth();
  renderFilters();
})();
