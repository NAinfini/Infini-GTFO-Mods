import assert from 'node:assert/strict';
import {graphMetadataVectors} from './graph-metadata-vectors.mjs';

/**
 * Audit real definitions by owner; never rewrite ownership or strip unsupported graph fields.
 * A shared-owner definition (e.g. forge.contract.combat) is registered by the SDK only when a module
 * really provides its binding. Whatever the SDK registers must equal the website record whole, because
 * the website merge rejects any semantic difference; the rest stay website authoring contracts.
 */
export function auditAuthoringContracts(logic, canonical, check, graphApi) {
    const cases = [];
    for (const row of canonical.registry.capabilities) {
        const definition = logic.capabilities.find(candidate => candidate.id === row.id);
        if (definition) {
            assert.deepEqual(row, definition, 'SDK canonical must equal the locked website definition: ' + row.id);
            check(true, 'SDK canonical equals the locked website definition: ' + row.id);
        }
    }
    for (const definition of logic.capabilities) {
        const provider = logic.providers.find(row => row.id === definition.owner);
        check(Boolean(provider), 'actual capability owner: ' + definition.id);
        const shared = canonical.registry.capabilities.some(row => row.id === definition.id);
        if (shared) {
            const sdkProvider = canonical.registry.providers.find(row => row.id === definition.owner);
            assert.deepEqual(sdkProvider, provider, 'SDK provider must equal the website provider: ' + definition.owner);
            check(true, 'shared canonical preserved: ' + definition.id);
        } else {
            check(definition.parameters.support === 'authoring-contract-only', 'authoring-only contract stays unregistered: ' + definition.id);
        }
        cases.push({id:definition.id, version:definition.version, shared,
            accepted:true, expectedCode:null,
            seed:{providers:[provider], capabilities:[definition], bindings:[]}});
    }
    return graphMetadataVectors({schemaVersion:1, kind:'actual-authoring-registration-audit', gameVerified:false, cases}, graphApi, check);
}
