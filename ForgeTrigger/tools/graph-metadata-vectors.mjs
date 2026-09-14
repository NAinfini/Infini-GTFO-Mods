import assert from 'node:assert/strict';

/** Test data from current authoring definitions; no fields are stripped for the SDK. */
export function graphMetadataVectors(report, api, check) {
    const valid = report.cases;
    const graphOf = row => row.seed.capabilities[0].graph;
    // Whole-side variadic and bounded port groups both expand one numbered block from a structural count.
    const groupsOf = graph => [...(graph.variadic ? [{side: graph.variadic.side, parameter: graph.variadic.parameter}] : []),
        ...(graph.portGroups ?? []).map(group => ({side: group.side, parameter: group.parameter}))];
    const variable = valid.filter(row => groupsOf(graphOf(row)).length > 0);
    check(variable.length > 0, 'variable-port definitions from the website are exercised: ' + variable.length);
    for (const row of variable) {
        const graph = graphOf(row);
        const [{side, parameter}] = groupsOf(graph);
        const count = graph.parameters.find(p => p.id === parameter);
        const base = api.resolveGraphContract(graph, {})[side].length;
        row.resolutions = [undefined, count.minimum, count.minimum + 1, count.maximum].map(value => {
            const parameters = value === undefined ? {} : {[parameter]: value};
            const expected = api.resolveGraphContract(graph, parameters);
            check(expected[side].length === base + (value ?? count.minimum) - count.minimum, 'resolved port count: ' + row.id);
            return {parameters, expected};
        });
        row.invalidResolutions = [count.minimum - 1, count.maximum + 1, 2.5, '3', null, true].map(value => {
            const parameters = {[parameter]: value};
            assert.throws(() => api.resolveGraphContract(graph, parameters));
            check(true, 'invalid authoring arity: ' + row.id);
            return {parameters, expectedCode:'invalid-integer'};
        });
    }
    const source = valid.find(row => graphOf(row).variadic);
    assert.ok(source, 'at least one whole-side variadic definition is required for the negative metadata cases');
    const countOf = graph => graph.parameters.find(p => p.id === graph.variadic.parameter);
    const negative = [];
    function reject(name, mutate, code, detail) {
        const row = structuredClone(source);
        delete row.resolutions; delete row.invalidResolutions;
        row.caseId = name; row.accepted = false; row.shared = false;
        row.expectedCode = code;
        if (detail) row.expectedDetail = detail;
        const graph = graphOf(row); mutate(graph);
        assert.throws(() => api.validateGraphMetadata(graph, name));
        check(true, 'invalid graph metadata rejected by website: ' + name);
        negative.push(row);
    }
    reject('unknown-side', g => {g.variadic.side = 'both';}, 'variadic-side');
    reject('missing-count', g => {g.variadic.parameter = 'missing';}, 'variadic-count-parameter');
    reject('required-count', g => {countOf(g).required = true;}, 'variadic-count-parameter');
    reject('lower-bound-one', g => {countOf(g).minimum = 1;}, 'invalid-integer');
    reject('upper-bound-33', g => {countOf(g).maximum = 33;}, 'invalid-integer');
    reject('empty-expansion', g => {countOf(g).maximum = 2;}, 'invalid-integer');
    reject('missing-upper-bound', g => {delete countOf(g).maximum;}, 'variadic-count-bounds');
    reject('wrong-base-count', g => {g[g.variadic.side].pop();}, 'variadic-base-count');
    reject('mismatched-base-type', g => {const port = g[g.variadic.side][1]; port.type = port.type === 'boolean' ? 'number' : 'boolean';}, 'variadic-port-contract');
    reject('optional-template', g => {g.variadic.port.optional = true;}, 'variadic-template');
    reject('nullable-template', g => {g.variadic.port.nullable = true;}, 'variadic-template');
    reject('invalid-template-name', g => {g.variadic.port.id = '3invalid';}, 'port-name');
    reject('generated-port-collision', g => {g[g.variadic.side][0].id = g.variadic.port.id + '_3';}, 'variadic-port-conflict');
    reject('unknown-variadic-field', g => {g.variadic.unreviewed = true;}, 'unknown-field', 'unreviewed');
    reject('unknown-graph-field', g => {g.unreviewed = true;}, 'unknown-field', 'unreviewed');
    return {...report, definitionCount:valid.length, variablePortDefinitions:variable.map(row => row.id),
        invalidMetadataCases:negative.length,
        note:'Registration and read-only port expansion are verified separately from graph execution.', cases:[...valid,...negative]};
}
