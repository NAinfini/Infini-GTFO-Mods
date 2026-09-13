import assert from 'node:assert/strict';

/** Test data from current authoring definitions; no fields are stripped for the SDK. */
export function graphMetadataVectors(report, api, check) {
    const valid = report.cases;
    const variadic = valid.filter(row => row.seed.capabilities[0].graph.variadic);
    check(variadic.length === 9, 'all nine reviewed variadic 1.1 contracts are exercised');
    for (const row of variadic) {
        const graph = row.seed.capabilities[0].graph;
        check(row.version === '1.1.0', 'exact variadic revision: ' + row.id);
        const name = graph.variadic.parameter;
        row.resolutions = [undefined, 2, 3, 10, 32].map(count => {
            const parameters = count === undefined ? {} : {[name]: count};
            const expected = api.resolveGraphContract(graph, parameters);
            check(expected[graph.variadic.side].length === (count ?? 2), 'resolved port count: ' + row.id);
            return {parameters, expected};
        });
        row.invalidResolutions = [0, 1, 33, 2.5, '3', null, true].map(count => {
            const parameters = {[name]: count};
            assert.throws(() => api.resolveGraphContract(graph, parameters));
            check(true, 'invalid authoring arity: ' + row.id);
            return {parameters, expectedCode:'invalid-integer'};
        });
    }
    const source = variadic[0];
    const graphOf = row => row.seed.capabilities[0].graph;
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
    reject('mismatched-base-type', g => {g[g.variadic.side][1].type = 'boolean';}, 'variadic-port-contract');
    reject('optional-template', g => {g.variadic.port.optional = true;}, 'variadic-template');
    reject('nullable-template', g => {g.variadic.port.nullable = true;}, 'variadic-template');
    reject('invalid-template-name', g => {g.variadic.port.id = '3invalid';}, 'port-name');
    reject('generated-port-collision', g => {g[g.variadic.side][0].id = g.variadic.port.id + '_3';}, 'variadic-port-conflict');
    reject('unknown-variadic-field', g => {g.variadic.unreviewed = true;}, 'unknown-field', 'unreviewed');
    reject('unknown-graph-field', g => {g.unreviewed = true;}, 'unknown-field', 'unreviewed');
    return {...report, definitionCount:valid.length, invalidMetadataCases:negative.length,
        note:'Registration and read-only port expansion are verified separately from graph execution.', cases:[...valid,...negative]};
}
