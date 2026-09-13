import fs from 'node:fs';
import path from 'node:path';
import {pathToFileURL} from 'node:url';
import {createHash} from 'node:crypto';

const [site, output] = process.argv.slice(2);
if (!site || !output) throw new Error('Usage: node generate-wire.mjs <website-root> <output.json>');
const target = path.join(site, 'site', 'forge');
const {reference} = await import(pathToFileURL(path.join(target, 'target-schema.ts')).href);
const {indexTargetWorld, selectEffectRecipients} = await import(pathToFileURL(path.join(target, 'targeting.ts')).href);
const ref = (id = 'test.entity:1', lifeEpoch = 1, worldEpoch = 1) => ({id, worldEpoch, lifeEpoch});
const entity = (r = ref(), faction = 'blue', kind = 'enemy') =>
    ({ref: r, kind, faction, lifeState: 'alive', tags: ['tag'], receives: ['combat.health'], position: [1, 2, 3]});
const cases = [];
function run(kind, input) {
    if (kind === 'reference') { reference(input, 'fixture'); return; }
    if (kind === 'snapshot') { indexTargetWorld({worldEpoch: input?.ref?.worldEpoch ?? 1, entities: [input], relations: [], actors: {}}); return; }
    if (kind === 'actors') { indexTargetWorld({worldEpoch: 1, entities: [], relations: [], actors: input}); return; }
    if (kind === 'relations') { indexTargetWorld({worldEpoch: 1, entities: [], relations: input, actors: {}}); return; }
    if (kind !== 'relationship') throw new Error('Unknown kind');
    const {anchor, recipient, rules} = input;
    const entities = anchor.ref.id === recipient.ref.id ? [anchor] : [anchor, recipient];
    const world = {worldEpoch: anchor.ref.worldEpoch, entities, relations: rules, actors: {self: anchor.ref}};
    for (const relation of ['self', 'ally', 'hostile', 'neutral', 'unknown']) {
        const policy = {schemaVersion: 1, anchor: 'self', kinds: 'any', relations: [relation],
            lifeStates: ['alive', 'downed', 'dead'], requireTags: [], excludeTags: [], sort: 'stable-id', maxTargets: 256};
        if (selectEffectRecipients([recipient.ref], policy, world).selected.length) return relation;
    }
    throw new Error('No relation resolved');
}
function add(id, kind, input, expected = true, runtimeAccept = expected) {
    let websiteAccept = true, relation;
    try { relation = run(kind, input); } catch { websiteAccept = false; }
    if (websiteAccept !== expected) throw new Error('Unexpected website behavior: ' + id);
    cases.push({id, kind, input, websiteAccept, runtimeAccept, ...(relation ? {relation} : {})});
}
add('reference-large-game-id', 'reference', ref('entity:18446744073709551615'));
add('reference-unicode', 'reference', ref('entity:敌人😀'));
add('reference-zero-epochs', 'reference', ref('entity:0', 0, 0));
add('reference-safe-epochs', 'reference', ref('entity:1', Number.MAX_SAFE_INTEGER, Number.MAX_SAFE_INTEGER));
add('reference-256-text', 'reference', ref('x'.repeat(256)));
for (const [name, value] of [['empty', ''], ['leading-space', ' x'], ['trailing-space', 'x '], ['257-text', 'x'.repeat(257)]])
    add('reference-' + name, 'reference', ref(value), false);
for (const [name, value] of [['negative', -1], ['fraction', 1.5], ['unsafe', Number.MAX_SAFE_INTEGER + 1]]) {
    add('life-' + name, 'reference', ref('entity:1', value), false);
    add('world-' + name, 'reference', ref('entity:1', 1, value), false);
}
add('numeric-id-rejected', 'reference', {id: 7, worldEpoch: 1, lifeEpoch: 1}, false);
add('missing-life', 'reference', {id: 'entity:1', worldEpoch: 1}, false);
add('unknown-ref-field', 'reference', {...ref(), pointer: '123'}, false);
// Retained divergences: never normalize text or widen Runtime acceptance to hide them.
for (const [name, value] of [['newline', 'x\ny'], ['tab', 'x\ty'], ['nul', 'x\0y'], ['del', 'x\u007fy'], ['nel-edge', '\u0085x']])
    add('text-divergence-' + name, 'reference', ref(value), true, false);
add('text-divergence-bom-edge', 'reference', ref('\uFEFFx'), false, true);
add('snapshot-valid', 'snapshot', entity());
add('snapshot-custom-kind', 'snapshot', entity(ref(), 'blue', 'custom-kind'));
add('snapshot-null-faction', 'snapshot', entity(ref(), null));
for (const lifeState of ['alive', 'downed', 'dead']) add('snapshot-life-' + lifeState, 'snapshot', {...entity(), lifeState});
