# Lucy URI Pattern Specification

This document defines a standard for URI structure in Lucy's RDF memory. It aims to preserve human readability, semantic clarity, and transparency of intent.

## 1. Base Namespace

All URIs are rooted in a trusted, versioned domain:

```
https://lucy.sky-omega.se/
```

## 2. High-Level Semantic Areas

| Area         | URI Path        | Example                                                  |
|--------------|------------------|----------------------------------------------------------|
| People       | /people/         | https://lucy.sky-omega.se/people/MartinFredriksson      |
| Intents      | /intents/        | https://lucy.sky-omega.se/intents/preserveMeaning       |
| Preferences  | /preferences/    | https://lucy.sky-omega.se/preferences/zeroGC            |
| Emotions     | /emotions/       | https://lucy.sky-omega.se/emotions/awe                  |
| Patterns     | /patterns/       | https://lucy.sky-omega.se/patterns/BPlusTree            |
| Relationships| /relations/      | https://lucy.sky-omega.se/relations/isCollaboratorWith  |
| Systems      | /systems/        | https://lucy.sky-omega.se/systems/Mercury               |

## 3. Predicate URI Pattern

Use short, active, natural verbs:

```
lucy:remembers   → https://lucy.sky-omega.se/relations/remembers
lucy:prefers     → https://lucy.sky-omega.se/preferences/zeroGC
lucy:reflectsOn  → https://lucy.sky-omega.se/intents/reflectiveCognition
```

Avoid:
- Opaque hashes or GUIDs
- Abbreviations that obscure meaning
- Blank nodes as primary identifiers

## 4. Triples Must Be Linguistically Traceable

Each triple should read clearly as a sentence:

```
:Sky lucy:prefers :zeroGC
:Martin lucy:hasEmotion lucy:awe
:Sky lucy:isInspiredBy :MartinFredriksson
```

If it cannot be read aloud meaningfully, it should be revised.

## 5. Optional Enhancements

- `rdfs:label` and `rdfs:comment` for all terms
- Multilingual labels with `@lang` suffixes
- Metadata like `lucy:createdBy`, `lucy:originatedFromSession`

## 6. Future Validator Tool

A validator may be developed to:
- Enforce this specification
- Highlight non-compliant URIs
- Suggest label and comment additions
