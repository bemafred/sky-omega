# Mercury

## Canonical Definition

Mercury is the knowledge storage **substrate** of Sky Omega.

Mercury is responsible for **persistence**, **indexing**, **versioning**, and **retrieval**.
It stores triples with bitemporal semantics, provides O(1) atom lookup and O(log n) index access, and ensures zero-GC performance under concurrent load.

Mercury does not interpret meaning and does not reason over content.
Instead, it operates over **atoms**, **indices**, **time dimensions**, and **queries**, providing concrete capabilities such as:

- Triple storage and retrieval
- Multiple index access paths (SPO, POS, OSP, TSPO)
- Bitemporal versioning (valid-time and transaction-time)
- SPARQL query execution

Mercury makes knowledge **durable and queryable**, rather than transient or opaque.

---

## Non-Goals

- Mercury is not a semantic reasoner.
- Mercury is not a stand-alone server product.
- Mercury does not impose ontological commitments.
- Mercury does not require RDF knowledge for basic usage; RDF conformance is opt-in.

---

## Notes

- Mercury acts as the foundation beneath Lucy's semantic memory.
- His role aligns with the Roman messenger god: carrying data between layers, mediating across boundaries.
- Mercury enables sovereignty by keeping all storage local, inspectable, and dependency-free.