# Time Is Not Identity
## A Cookbook for Temporal Data Modeling

*A practical guide to reasoning about time without inventing unnecessary complexity.*

**Version 0.2 — 13 November 2026**

*Revision note: Adds an explicit separation between domain knowledge and agent-behavior diagnostics, and derives same-clock membership behavior from set semantics rather than event-history assumptions. The Mercury examples are modeling guidance, not a replacement for its ADRs.*

### Preface: The Timestamp That Wasn't a Problem

Consider a system that stores facts.

A fact is added, removed, and added again. Perhaps these operations occur within the same millisecond.

An engineer notices that two operations may receive identical timestamps and raises an alarm:

*How can we distinguish them? We need more precise timestamps, sequence numbers, or unique version identifiers!*

Perhaps.

But before solving that problem, the engineer has neglected to ask a more fundamental question:

**What, exactly, needs to be distinguished — and why?**

If the system represents a set of facts, it does not necessarily represent a sequence of operations.

A memorize-and-forget sequence within one clock reading may be an observable *client behavior* without producing any nonempty interval of remembered *knowledge*. The database need not turn the behavior into another fact merely because it can detect it.

The engineer may be attempting to preserve distinctions that do not exist in the domain model.

This is a recurring failure in temporal data modeling: importing mechanisms from familiar architectures before establishing the semantics of the data.

The purpose of this cookbook is to prevent that failure.

It begins with a rule:

> **Never introduce an identity, ordering, or precision requirement that the domain semantics do not demand.**

---

# Part I — Establish the Ontology

## Recipe 1: Determine What Exists Before Giving It an Identity

**Problem:** A design introduces identifiers for things whose independent existence has not been established.

**Temptation:** Assign an identifier to every record, write, version, or occurrence.

**Method:**

Before designing a key, determine what the key identifies.

Consider the RDF triple:

- Subject: Alice
- Predicate: knows
- Object: Bob

In an RDF graph, that triple is a member of a set.

Asserting it twice does not create two separate facts.

An RDF graph does not contain two occurrences of the same RDF triple. It either contains the triple or it does not.

The distinction is fundamental:

- A fact is a proposition represented by RDF terms.
- An assertion operation may introduce that fact into a graph.
- A storage record is one possible physical representation.
- A historical observation records something about the fact or an operation.

These are different entities.

Do not grant all four independent identities merely because an implementation could do so.

**Design test:** If two records contain exactly the same logical fact, what domain-level question requires treating them as different?

If there is no answer, an additional identity may be unnecessary.

**Important qualification:** RDF graphs have set semantics, but SPARQL query results use multiset semantics unless explicitly reduced. Do not confuse the two.

---

## Recipe 2: Distinguish a Set From a Sequence

**Problem:** A set-based system is accidentally designed as an event log.

Let S be a set of facts, and let q be a fact.

Adding q produces:

S' = S ∪ {q}

Removing q produces:

S' = S ∖ {q}

These operations are idempotent.

Adding an already-present fact does not change the set. Removing an absent fact does not change the set.

Consequently:

- Add(q), Add(q) has the same final set as Add(q).
- Remove(q), Remove(q) has the same final set as Remove(q).
- The current set does not reveal how many times either operation was attempted.

This is not information loss from the perspective of set semantics. The operation count was never part of the represented set.

An event log is different.

An event log may distinguish every addition attempt, removal attempt, retry, and acknowledgment.

Neither model is inherently superior. They answer different questions.

**Design test:** Does the application need to know *which facts belong to the set*, or *which operations occurred and in what order*?

Do not implement the second merely to support the first.

---

## Recipe 3: Do Not Confuse Equality With Historical Existence

**Problem:** An engineer assumes that because a fact can disappear and reappear, each appearance must be a new fact with a new identity.

Consider a fact q that belongs to a set at two different periods.

The fact remains q.

Its membership may change over time, but the identity of the fact need not.

A temporal system can be described abstractly as:

S(t) = the set of facts present at time t.

The same q may be a member of S(t₁), absent from S(t₂), and a member again at S(t₃).

Nothing in this definition requires three different identities for q.

However, if the application must distinguish separate periods of membership, its temporal representation must preserve that distinction at the chosen level of observation.

The key insight is:

**Fact identity, temporal membership, and operation history are different concepts.**

A membership interval is a way to describe when a fact belongs to a set. It is not automatically a separately identified entity. Introduce an interval identifier only if the domain actually requires one.

Do not conflate them.

**Design test:** If a fact disappears and reappears, is the application asking about the fact, its periods of membership, or the events that changed its membership?

Choose the representation only after answering.

---

# Part II — Give Time a Meaning

## Recipe 4: Define What Every Timestamp Measures

**Problem:** A column named `Timestamp` exists without a precise definition.

A timestamp can refer to many different phenomena:

- When an event occurred in the world.
- When somebody observed that event.
- When information was received.
- When information was accepted as knowledge.
- When a fact entered a particular collection.
- When a fact ceased to belong to that collection.
- When a database transaction committed.
- When a physical record was written.
- When a response was generated.

These times may coincide. They are not thereby equivalent.

Example:

Alice was born in 1990.

A system learns this in 2026.

The birth occurred in 1990. The system acquired the information in 2026.

Neither timestamp can substitute for the other.

**Method:**

For every temporal field, finish this sentence:

"This timestamp records when _____."

If the blank cannot be filled unambiguously, the schema is incomplete.

Prefer names that express meaning rather than generic technical vocabulary.

**Design test:** Could a reader determine exactly what the timestamp means without examining the code that populates it?

---

## Recipe 5: Separate World Time From Knowledge Time

**Problem:** A system confuses when something happened with when it became known.

Suppose James learns on November 12 that a software defect existed on November 5.

Two different statements are involved:

1. The defect existed on November 5.
2. James learned about the defect on November 12.

The first concerns the world being modeled.

The second concerns the epistemic state of the observer.

An architecture for persistent memory may need to record both, but it need not represent both as dedicated temporal dimensions of the storage engine.

World-event time may be part of the RDF facts themselves.

Knowledge time may be metadata attached to membership in a memory.

This is a legitimate separation of responsibilities.

**Anti-pattern:** Inferring the time of a world event from the time at which a database stored information about it.

**Design test:** Can the system represent learning something today about an event that happened decades ago without retroactively changing the acquisition time?

---

## Recipe 6: Do Not Assume That Every Temporal Model Is Bitemporal

**Problem:** An implementation adopts conventional database terminology without verifying that the underlying semantics match.

Traditional bitemporal databases distinguish two time dimensions, commonly:

- Valid time: when information applies in the modeled world.
- Transaction time: when information is recorded in the database.

Other systems may use different temporal models.

For example, a memory system may describe when a fact entered memory and when it left memory.

Those are endpoints of a period of membership in the same temporal dimension, not necessarily two independent dimensions.

The presence of two timestamps does not make a system bitemporal.

Neither does the ability to reconstruct historical states.

**Method:**

Define the temporal relation mathematically before assigning it a database-industry name.

**Design test:** Are the proposed temporal dimensions independent, or are they boundaries of one interval?

---

## Recipe 7: Treat Timestamp Precision as a Requirement, Not an Identity Mechanism

**Problem:** A design increases timestamp precision to resolve an alleged identity problem.

Millisecond timestamps are replaced by microseconds.

Then by nanoseconds.

Then by a sequence number.

Eventually, the timestamp has become a disguised event identifier.

This may solve a real problem — or it may solve one that the domain never had.

For a set-based model, several operations may be observationally equivalent at the chosen temporal resolution.

If the temporal contract describes set membership at representable clock readings, a sequence within one reading need not create separately queryable membership periods. Increasing clock precision or manufacturing operation identifiers would change the contract, not repair an RDF identity defect.

The converse also matters: a different system may explicitly promise every transition, including ones within one clock tick. That is an event-history or finer-grained observation requirement, and its representation must support that promise. **Do not silently impose that requirement on a temporal set.**

**Method:**

Ask three questions:

1. What temporal distinctions does the domain require?
2. What observations must queries be able to recover?
3. What distinctions may safely be collapsed?

Choose temporal resolution only after establishing these requirements.

**Design test:** If two operations receive the same timestamp, which required query becomes impossible to answer?

If no such query exists, do not manufacture a collision problem.

If such a query exists, specify the missing information explicitly rather than assuming higher clock precision is always the answer.

**Same-clock example (for a temporal RDF set):** A newly memorized quad that is forgotten within the same clock reading has no nonempty `[m, f)` membership period when `m = f`. No zero-duration historical version is needed. If a quad is forgotten and then memorized again within the same reading, the intermediate removal need not split its membership period. This is a *temporal observation policy*, not a claim that the operations never occurred.

**Concurrency caveat:** A clock reading is not inherently a transaction or isolation boundary. Live readers may observe intermediate operations if the write/read contract permits it; that separate visibility contract does not entail retaining every intermediate operation as historical RDF membership.

---

# Part III — Model Temporal State

## Recipe 8: Define Interval Boundaries Explicitly

**Problem:** A system stores start and end times but does not specify whether the endpoints are inclusive.

A common convention is the half-open interval:

[start, end)

This means a fact is present at the start but absent at the end.

For a membership interval with start m and end f:

m ≤ t < f

An open-ended interval can represent current membership.

This convention is useful because adjacent intervals meet without overlapping at their shared boundary.

However, the convention alone does not determine the meaning of simultaneous changes, clock granularity, repeated operations, or visibility to concurrent readers.

Those require separate decisions. In particular, `[m, m)` is empty; storing it as a historical membership period would add a distinct semantic promise beyond ordinary interval membership.

**Method:**

Specify the boundary rule once and apply it consistently throughout storage, queries, serialization, and tests.

**Design test:** At the exact time a fact ceases to belong to a set, does the query return it?

There must be one unambiguous answer.

---

## Recipe 9: Define Historical State Before Implementing Historical Queries

**Problem:** A query feature is designed around familiar syntax rather than the information the system must recover.

A useful abstract requirement is:

Given time t, reconstruct S(t).

For a memory, this may mean:

"What facts belonged to this memory at time t?"

That question is independent of SQL syntax, SPARQL extensions, or internal indexing.

A second question is:

"During which periods did q belong to memory?"

These are related but different queries.

A third question is:

"What operations caused the membership to change?"

That question may require an event model and need not be answerable by a temporal set store.

**Method:**

Define supported queries in domain language before choosing syntax.

Then identify the minimum information required to answer them.

**Design test:** Can the proposed representation answer every promised historical query without inventing unavailable information?

---

## Recipe 10: Make Forgetting a Precisely Defined Operation

**Problem:** The word "forget" is used interchangeably for removing, invalidating, contradicting, archiving, or destroying information.

These have different semantics.

A memory may cease to contain q while preserving a **nonempty** historical period during which q was remembered. A newly added fact removed in the same clock reading need not leave any such period.

A later assertion may contradict q without necessarily removing q.

A retention process may permanently destroy historical records.

These are distinct operations.

**Method:**

Specify which observable state each operation changes.

For example:

- Remove from current membership.
- Preserve or discard historical membership.
- Record contradictory information independently.
- Permanently erase information under an explicit retention policy.

Do not conceal these differences behind one ambiguous method name.

**Design test:** If a fact is forgotten, can the system still answer whether it once remembered that fact?

The answer must follow from the contract, not from an accidental property of the storage implementation.

---

# Part IV — Keep Semantics Separate From Machinery

## Recipe 11: Never Invent Metadata to Fill an API

**Problem:** A unified API requires every result to contain temporal fields, even when the underlying profile stores no temporal information.

An implementation chooses convenient values:

- Zero
- Minimum timestamp
- Maximum timestamp
- Current time
- A fabricated default interval

Consumers may interpret these values as genuine observations.

The API has manufactured knowledge.

**Method:**

Distinguish:

- Information that exists.
- Information that exists but is unknown.
- Information that is not applicable.
- Information that was never recorded.

Use a type system capable of preserving those distinctions.

A profile that stores ordinary RDF quads should not need to pretend it stores temporal membership metadata.

**Design test:** Can every temporal value returned by the API be traced to actual stored information or an explicitly defined computation?

---

## Recipe 12: Physical Representation Must Not Define Logical Identity

**Problem:** An index key, memory address, row identifier, or binary layout is mistaken for the identity of a domain object.

Physical storage may employ:

- B+Trees
- Sorted composite keys
- Hash tables
- Memory-mapped files
- Version records
- Append-only segments
- Compression and deduplication

Those are implementation mechanisms.

Their keys may include auxiliary information needed to sort, locate, or manage records.

That does not automatically make those fields part of the domain's logical identity.

**Method:**

Define logical equality independently of physical storage.

Then design indexes to implement the required operations.

A storage migration must be able to change physical keys without silently changing what the application considers the same fact.

**Design test:** Would changing the index layout change the logical identity or equality of a fact?

If yes, determine whether the coupling is intentional.

---

## Recipe 13: Distributed Time Is Not Automatically Distributed Causality

**Problem:** A distributed system assumes that wall-clock timestamps establish the causal order of events across machines.

Different clocks may disagree.

A later causal event may carry an earlier wall-clock timestamp.

If causal ordering matters, the system may require additional information, such as logical clocks, causal dependencies, or an agreed serialization point.

But many distributed operations do not require a total ordering.

In particular, set membership and replicated data semantics should be established before introducing global event sequences.

**Method:**

Distinguish:

- Chronological measurements.
- Causal relationships.
- Logical ordering requirements.
- Set convergence requirements.

Do not solve all four with a single timestamp field.

**Design test:** Does the system need a global total order, or merely a well-defined resulting state?

---

# Part V — Test the Semantics, Not the Implementation

## Recipe 14: Write Invariants Before Algorithms

Temporal designs should begin with properties that must remain true regardless of implementation.

For a set-based RDF system, useful foundational properties include:

**Set uniqueness**

An identical RDF fact cannot occur twice in the same logical graph state.

**Addition idempotence**

Adding an existing fact does not alter the resulting set.

**Removal idempotence**

Removing an absent fact does not alter the resulting set.

**State consistency**

A historical-state query must return exactly the facts defined as members at that time.

**Boundary consistency**

Every interval boundary obeys the specified inclusion convention.

**Temporal provenance**

A returned timestamp must have the meaning declared by its schema.

**Representation independence**

Changing storage layout must not change logical query results.

**Replay independence, where promised**

Two histories that are equivalent under the domain's defined semantics must produce equivalent observable results.

This last property requires care: histories with identical current sets need not be equivalent for a system that also promises historical membership queries.

**Method:**

Implement these as property-based tests, model-based tests, or executable examples.

Test deliberately difficult cases:

- Repeated addition of an existing fact.
- Repeated removal of an absent fact.
- Removal followed by readdition.
- Equal timestamps, including memorize-then-forget in one reading (no zero-length membership period).
- Forget-then-memorize in one reading (no artificial gap for an otherwise continuing period).
- An absent fact memorized, forgotten, and memorized again in one reading (one resulting period, not an event sequence).
- An existing fact forgotten, memorized, and forgotten in one reading (correct final membership without synthetic history).
- Adjacent intervals.
- Open-ended intervals.
- Different world-event and observation times.
- Storage migration.
- Different physical platforms.
- Competing updates, where concurrency is supported.

A passing test demonstrates conformity to a specified invariant. It does not establish the correctness of an unstated assumption.

---

## Recipe 15: Do Not Confuse Observable Agent Behavior With Domain Knowledge

**Problem:** A memory system detects suspicious client operations and preserves them as extra RDF history, even though they do not express additional knowledge in its domain.

**Example:** An agent submits `Memorize(q)` followed by `Forget(q)` within a single clock reading, for a quad that was previously absent.

Under a temporal set-membership model with half-open intervals, the proposed membership period is `[m, m)`, which is empty. There is no interval during which q is remembered at the represented temporal granularity.

Nevertheless, the operations happened. That fact may be useful **as a diagnostic about the client**, rather than as a historical RDF membership fact. Treating it as a zero-length memory period would cross an architectural boundary: it would turn an observation of agent behavior into purported memory history.

**Method:** Separate three layers explicitly.

| Layer | Represents | Example question |
|---|---|---|
| Domain / RDF memory | Which RDF quads belong to memory at represented times | “Was q remembered at t?” |
| Runtime diagnostics | Which operations, retries, anomalies, or errors were observed | “Did the client immediately undo its own write?” |
| Agent reasoning / governance | Why the behavior occurred and whether it is warranted | “Did James make a poor memory-management decision?” |

A diagnostic log can report something like `MemoryChurnWithinClock`, **only if the implementation actually observed the relevant operations**. Equal timestamps in stored rows alone do not prove misbehavior. Do not automatically treat all same-clock churn as an error: retries, batch semantics, and legitimate corrections may account for it. The interpretation belongs at the behavioral layer.

A diagnostic entry is not automatically RDF knowledge about the external world. It could become a deliberate RDF statement *about an agent's observed behavior* if such provenance is part of another explicitly defined knowledge domain; that requires its own semantics, not an accidental side effect of storage.

In the Sky Omega architecture, Mercury can expose a structured observation, while James can investigate the cause. DrHook or other runtime-observation tools may supply corroborating evidence. None of these obligations requires Mercury to retain a zero-duration RDF memory version.

**Design tests:**

- Does the RDF query layer return the same temporal membership for histories equivalent under its contract?
- Can diagnostics describe a suspicious operation sequence without changing RDF membership?
- Is diagnostic emission based on observed operations rather than inferred solely from timestamp equality?
- Can the agent distinguish a behavioral anomaly from a demonstrated reasoning defect?
- Can diagnostics be disabled or retained separately without changing domain-query answers?

**Rule:**

> **Do not preserve an operational anomaly as domain knowledge merely because it is observable. Observe it through diagnostics; interpret it at the layer responsible for the behavior.**

---

# Part VI — The Temporal Design Review

Before approving a design that introduces time, answer the following questions.

| Question | Required clarification |
|---|---|
| What is the fundamental entity? | Fact, event, observation, state, or something else |
| What determines identity? | Explicit logical equality |
| Is the collection a set? | Whether duplicates have meaning |
| Is order relevant? | Which domain queries require it |
| What does each timestamp mean? | Exact temporal semantics |
| What is the temporal resolution? | Required observable distinctions |
| Are intervals used? | Boundary and open-end rules |
| What is retained? | Current state, historical state, operations |
| What can be reconstructed? | Explicit query guarantees |
| How is time obtained? | Clock source and consistency contract |
| What does a missing timestamp mean? | Unknown, unavailable, or inapplicable |
| What belongs to storage? | Logical data versus implementation machinery |
| Which invariants apply? | Executable acceptance criteria |
| Is an anomaly domain knowledge? | Whether it changes set membership or merely describes client behavior |
| Who observes and interprets anomalies? | Diagnostic instrumentation versus agent reasoning |
| What can concurrent readers see? | Visibility and isolation guarantees, independently of clock resolution |

A design is not complete merely because it answers all these questions.

But if it cannot answer them, implementation is premature.

---

# Part VII — A Worked Example: Cognitive Memory

Consider a system that remembers RDF facts.

Its primary question is not necessarily:

"What sequence of database operations has occurred?"

It may instead be:

"What did this memory contain, and when?"

Define:

- Q: the universe of possible RDF quads.
- T: the temporal domain.
- M(t): the set of quads remembered at time t.

Then:

M : T → P(Q)

where P(Q) denotes the power set of Q.

This is a mathematical model of time-varying set membership.

It does not, by itself, require:

- Unique identifiers for individual insertion attempts.
- A total order over all physical writes.
- Separate logical identities for repeated assertions.
- A conventional bitemporal database.
- Event sourcing.
- Arbitrarily high-resolution timestamps.

Nor does it forbid any of those things.

They become justified only when additional requirements demand them.

For example, retaining every change operation is an additional requirement. So is preserving independently attributable observations from several agents. So is reconstructing every state transition at finer resolution than the system's timestamps can represent.

None follows automatically from the existence of temporal RDF data.

The next design task is to specify precisely how M(t) is represented, updated, and queried — including the treatment of changes that share a clock reading.

### An illustrative same-clock policy

Assume a chosen memory-time resolution, half-open membership intervals, and a historical query contract that does **not** preserve every intermediate client operation. For a quad `q`:

| Starting membership | Operations within one reading `t` | Resulting represented membership |
|---|---|---|
| Absent | `Memorize(q); Forget(q)` | Absent; no `[t, t)` period |
| Present | `Forget(q); Memorize(q)` | Present; existing period continues |
| Absent | `Memorize(q); Forget(q); Memorize(q)` | Present; one period begins at `t` |
| Present | `Forget(q); Memorize(q); Forget(q)` | Absent; preceding period ends at `t` |

These rows describe a *possible temporal-set policy* for the resulting membership, not an event-log reconstruction. They assume the indicated sequence is observed by the writer in that order. They do **not** assert that one clock reading is one atomic request, nor do they define what a concurrent live reader may see in between operations.

If the implementation can diagnose client churn, it may log it separately. It must not invent additional historical RDF facts to account for the diagnostic.

That separation belongs to the semantics of the system, not to assumptions imported from another architecture.

### The critical distinction

A fact's identity answers:

**What fact is this?**

Temporal set membership answers:

**Does this fact belong to the collection at this time?**

Event history answers:

**What happened to change the collection?**

Behavioral diagnostics answers:

**What did the client do, and what might warrant investigation?**

These questions may be related.

They are not interchangeable. In particular, behavioral observations are not automatically domain facts, and clock resolution does not define identity.

---

# Epilogue — Epistemology Before Engineering

There is a common failure mode in software development.

An engineer encounters a possible ambiguity.

Rather than establishing whether the ambiguity exists in the domain, the engineer immediately invents machinery to eliminate it.

An identifier is added.

A sequence number follows.

A transaction log appears.

An abstraction is introduced to coordinate them.

The system becomes more complicated, and the original question remains unanswered.

Advanced AI coding agents are not immune to this behavior. Their familiarity with established architectural patterns can make it more tempting to apply a recognizable solution before establishing that the problem exists.

The remedy is not less engineering competence.

It is greater epistemic discipline.

Before adding machinery:

1. Identify the entities.
2. Establish their logical identities.
3. Define the observable state.
4. Specify the semantics of time.
5. Derive the information that must be preserved.
6. Only then choose algorithms, structures, and representations.

This is Operational Epistemology applied to temporal data modeling.

The goal is not to build the most sophisticated temporal database.

It is to build the simplest system whose behavior faithfully represents its domain.

**Time is not identity.**

**A set is not an event log.**

**A timestamp is not an explanation.**

**An operational anomaly is not automatically domain knowledge.**

And a problem is not established merely because an engineer knows how to solve it.

---

*Canyala Research Foundation — Laboratory 1*

*Working draft. The same-clock policy is an illustrative interpretation of temporal RDF set membership; production guarantees, concurrency behavior, diagnostic retention, and Mercury-specific decisions must be established in their respective ADRs.*
