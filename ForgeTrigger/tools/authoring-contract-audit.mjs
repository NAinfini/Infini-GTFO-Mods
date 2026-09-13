import assert from 'node:assert/strict';
import {graphMetadataVectors} from './graph-metadata-vectors.mjs';

/** Audit real definitions by owner; never rewrite ownership or strip unsupported graph fields. */
export function auditAuthoringContracts(logic, canonical, check, graphApi) {
    const cases = [];
    for (const definition of logic.capabilities) {
        const provider = logic.providers.find(row => row.id === definition.owner);
        check(Boolean(provider), 'actual capability owner: ' + definition.id);
        const shared = definition.owner !== 'forge.contract.logic';
        if (shared) {
            const actual = canonical.registry.capabilities.find(row => row.id === definition.id);
            assert.deepEqual(definition, actual, 'shared canonical must match the compiled SDK: ' + definition.id);
            check(true, 'shared canonical preserved: ' + definition.id);
        } else {
            check(definition.parameters.support === 'authoring-contract-only', 'local authoring contract: ' + definition.id);
        }
        cases.push({id:definition.id, version:definition.version, shared,
            accepted:true, expectedCode:null,
            seed:{providers:[provider], capabilities:[definition], bindings:[]}});
    }
    return graphMetadataVectors({schemaVersion:1, kind:'actual-authoring-registration-audit', gameVerified:false, cases}, graphApi, check);
}
