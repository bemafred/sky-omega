# ADR-000 - Repository Restructure for Multi-Substrate Architecture

## Status
Implemented

## Context
Sky Omega is evolving from a single-substrate system (Mercury for RDF/knowledge) to a multi-substrate cognitive architecture. The addition of Minerva (tensor/inference substrate) requires a harmonized repository structure that:

1. Clearly separates substrates while showing they're siblings
2. Organizes documentation by type (ADRs, specs, architecture, API)
3. Provides consistent patterns for tests, benchmarks, examples
4. Supports Claude Code navigation and execution

## Decision

### Directory Structure

```
sky-omega/
├── CLAUDE.md
├── README.md
├── SkyOmega.sln
│
├── docs/
│   ├── adrs/
│   │   ├── mercury/
│   │   └── minerva/
│   ├── specs/
│   │   ├── rdf/           # Future: W3C spec summaries
│   │   └── llm/
│   ├── architecture/
│   ├── api/
│   └── scratches/
│       ├── ai-ideation/   # Renamed from ai-ideation-dialogue-scratches
│       ├── reasoning/     # Moved from repo root
│       └── semantic/      # Moved from repo root
│
├── src/
│   ├── Mercury/
│   ├── Mercury.Cli.Sparql/
│   ├── Mercury.Cli.Turtle/
│   ├── Mercury.Mcp/
│   ├── Mercury.Pruning/
│   │
│   ├── Minerva/
│   ├── Minerva.Cli/
│   └── Minerva.Mcp/
│
├── tests/
│   ├── Mercury.Tests/
│   └── Minerva.Tests/
│
├── benchmarks/
│   ├── Mercury.Benchmarks/
│   └── Minerva.Benchmarks/
│
└── examples/
    ├── Mercury.Examples/
    └── Minerva.Examples/
```

## Implementation Tasks

Execute these tasks in order. Check off each task as completed.

### Phase 1: Documentation Restructure

#### Task 1.1: Create new docs directory structure
```bash
mkdir -p docs/adrs/mercury
mkdir -p docs/adrs/minerva
mkdir -p docs/specs/rdf
mkdir -p docs/specs/llm
mkdir -p docs/architecture
mkdir -p docs/api
mkdir -p docs/scratches
```
- [ ] Directories created

#### Task 1.2: Move Mercury ADRs
```bash
# Move all mercury-adr-*.md files to docs/adrs/mercury/ (git mv preserves history)
git mv docs/mercury-adr-buffer-pattern.md docs/adrs/mercury/ADR-001-buffer-pattern.md
git mv docs/mercury-adr-compaction.md docs/adrs/mercury/ADR-002-compaction.md
git mv docs/mercury-adr-dual-mode-store-access.md docs/adrs/mercury/ADR-003-dual-mode-store-access.md
git mv docs/mercury-adr-quadstore-pool-unified.md docs/adrs/mercury/ADR-004-quadstore-pool-unified.md
git mv docs/mercury-adr-quadstore-pooling-and-clear.md docs/adrs/mercury/ADR-005-quadstore-pooling-and-clear.md
git mv docs/mercury-adr-service-scan-interface.md docs/adrs/mercury/ADR-006-service-scan-interface.md
git mv docs/mercury-adr-union-service-execution.md docs/adrs/mercury/ADR-007-union-service-execution.md
git mv docs/ibuffermanager-adoption-plan.md docs/adrs/mercury/ADR-008-ibuffermanager-adoption.md
```
- [ ] ADRs moved and renamed

#### Task 1.3: Move architecture docs
```bash
git mv docs/sky-omega-convergence.md docs/architecture/
git mv docs/sky-omega-identity.md docs/architecture/
git mv docs/eee-lexicon.md docs/architecture/
git mv docs/eee-manifesto.md docs/architecture/
git mv docs/temporal-rdf.md docs/architecture/
git mv docs/conclusions.md docs/architecture/
```
- [ ] Architecture docs moved

#### Task 1.4: Move API docs
```bash
git mv docs/api-usage.md docs/api/
```
- [ ] API docs moved

#### Task 1.5: Move scratch directories
```bash
# Rename and move ideation scratches
git mv docs/ai-ideation-dialogue-scratches docs/scratches/ai-ideation

# Move root-level scratch directories into docs/scratches/
git mv reasoning docs/scratches/reasoning
git mv semantic docs/scratches/semantic
```
- [ ] Scratches consolidated

#### Task 1.6: Create ADR index files
Create `docs/adrs/mercury/README.md`:
```markdown
# Mercury Architecture Decision Records

| ADR | Title | Status |
|-----|-------|--------|
| [ADR-001](ADR-001-buffer-pattern.md) | Buffer Pattern | Accepted |
| [ADR-002](ADR-002-compaction.md) | Compaction | Accepted |
| [ADR-003](ADR-003-dual-mode-store-access.md) | Dual-Mode Store Access | Proposed |
| [ADR-004](ADR-004-quadstore-pool-unified.md) | QuadStore Pool Unified | Proposed |
| [ADR-005](ADR-005-quadstore-pooling-and-clear.md) | QuadStore Pooling and Clear | Accepted |
| [ADR-006](ADR-006-service-scan-interface.md) | SERVICE Scan Interface | Accepted |
| [ADR-007](ADR-007-union-service-execution.md) | UNION SERVICE Execution | Accepted |
| [ADR-008](ADR-008-ibuffermanager-adoption.md) | IBufferManager Adoption | Completed |
```

Create `docs/adrs/minerva/README.md`:
```markdown
# Minerva Architecture Decision Records

| ADR | Title | Status |
|-----|-------|--------|
| [ADR-001](ADR-001-weight-formats.md) | Weight and Tokenizer Format Support | Proposed |
```
- [ ] ADR index files created

### Phase 2: Minerva Scaffold

#### Task 2.1: Create Minerva source structure
```bash
mkdir -p src/Minerva/Weights/Gguf
mkdir -p src/Minerva/Weights/SafeTensors
mkdir -p src/Minerva/Tokenizers
mkdir -p src/Minerva/Tensors
mkdir -p src/Minerva/Inference
mkdir -p src/Minerva.Cli
mkdir -p src/Minerva.Mcp
```
- [ ] Minerva source directories created

#### Task 2.2: Create Minerva test/benchmark/example structure
```bash
mkdir -p tests/Minerva.Tests
mkdir -p benchmarks/Minerva.Benchmarks
mkdir -p examples/Minerva.Examples
```
- [ ] Minerva test/benchmark/example directories created

#### Task 2.3: Create Minerva project file
Create `src/Minerva/Minerva.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <LangVersion>14.0</LangVersion>
    <RootNamespace>SkyOmega.Minerva</RootNamespace>
  </PropertyGroup>
</Project>
```
- [ ] Minerva.csproj created

#### Task 2.4: Create Minerva placeholder files
Create `src/Minerva/Weights/Gguf/GgufReader.cs`:
```csharp
namespace SkyOmega.Minerva.Weights.Gguf;

/// <summary>
/// Zero-copy GGUF file reader with memory-mapped access.
/// See docs/specs/llm/GGUF.md for format specification.
/// </summary>
public sealed class GgufReader : IDisposable
{
    // TODO: Implement per ADR-001-weight-formats.md
    public void Dispose() { }
}
```

Create `src/Minerva/Weights/SafeTensors/SafeTensorsReader.cs`:
```csharp
namespace SkyOmega.Minerva.Weights.SafeTensors;

/// <summary>
/// Zero-copy SafeTensors file reader with memory-mapped access.
/// See docs/specs/llm/SafeTensors.md for format specification.
/// </summary>
public sealed class SafeTensorsReader : IDisposable
{
    // TODO: Implement per ADR-001-weight-formats.md
    public void Dispose() { }
}
```

Create `src/Minerva/Tokenizers/Tokenizer.cs`:
```csharp
namespace SkyOmega.Minerva.Tokenizers;

/// <summary>
/// Tokenizer abstraction supporting BPE, SentencePiece, and GGUF-embedded tokenizers.
/// See docs/specs/llm/Tokenizers.md for format specifications.
/// </summary>
public abstract class Tokenizer
{
    public abstract ReadOnlySpan<int> Encode(ReadOnlySpan<char> text);
    public abstract string Decode(ReadOnlySpan<int> tokens);
}
```
- [ ] Minerva placeholder files created

### Phase 3: Specs Documentation

#### Task 3.1: Create GGUF spec document
Create `docs/specs/llm/GGUF.md`:
```markdown
# GGUF File Format Specification

## Source
- Official spec: https://github.com/ggml-org/ggml/blob/master/docs/gguf.md
- Reference implementation: llama.cpp

## Overview
GGUF (GGML Universal Format) is a binary format for storing ML models for inference.
Self-contained: weights, tokenizer, and metadata in a single file.

## File Structure

### Header
| Field | Type | Description |
|-------|------|-------------|
| Magic | uint32 | "GGUF" (0x46554747) |
| Version | uint32 | Format version (currently 3) |
| Tensor Count | uint64 | Number of tensors |
| KV Count | uint64 | Number of key-value metadata pairs |

### Key-Value Metadata
Key-value pairs providing model information.

```
struct gguf_kv {
gguf_str key;
gguf_type type;
gguf_value value;
};
```

### Tensor Info
```
struct gguf_tensor_info {
gguf_str name;
uint32_t n_dims;
uint64_t ne[4];      // dimensions
ggml_type type;
uint64_t offset;     // from start of data section
};
```

### Data Section
Raw tensor data, aligned to GGUF_DEFAULT_ALIGNMENT (32 bytes).
Memory-mappable for zero-copy access.

## Data Types
| Type | ID | Description |
|------|-----|-------------|
| F32 | 0 | 32-bit float |
| F16 | 1 | 16-bit float |
| Q4_0 | 2 | 4-bit quantized |
| Q4_1 | 3 | 4-bit quantized (variant) |
| Q5_0 | 6 | 5-bit quantized |
| Q5_1 | 7 | 5-bit quantized (variant) |
| Q8_0 | 8 | 8-bit quantized |
| Q8_1 | 9 | 8-bit quantized (variant) |
| ... | ... | See full spec |

## Implementation Notes for Minerva
- Use MemoryMappedFile for tensor data access
- Parse header eagerly, tensor data lazily
- Validate magic and version before processing
- Respect alignment for tensor offsets
```
- [ ] GGUF spec created

#### Task 3.2: Create SafeTensors spec document
Create `docs/specs/llm/SafeTensors.md`:
```markdown
# SafeTensors File Format Specification

## Source
- Official repo: https://github.com/huggingface/safetensors
- Core implementation: ~900 lines Rust

## Overview
SafeTensors is a simple, safe format for storing tensors.
No arbitrary code execution (unlike pickle).
Designed for zero-copy access and lazy loading.

## File Structure

```
[8 bytes: header_size (little-endian uint64)]
[header_size bytes: JSON header]
[remainder: tensor data]
```

### Header (JSON)
```json
{
  "__metadata__": { "format": "pt" },
  "tensor_name": {
    "dtype": "F32",
    "shape": [1024, 1024],
    "data_offsets": [0, 4194304]
  }
}
```

### Data Types
| dtype | Description |
|-------|-------------|
| F64 | 64-bit float |
| F32 | 32-bit float |
| F16 | 16-bit float |
| BF16 | bfloat16 |
| I64 | 64-bit signed int |
| I32 | 32-bit signed int |
| I16 | 16-bit signed int |
| I8 | 8-bit signed int |
| U8 | 8-bit unsigned int |
| BOOL | boolean |

## Constraints
- Little-endian byte order
- Row-major (C) order
- Buffer must be entirely indexed (no holes)
- Header limited to 100MB (DOS protection)
- Offsets must not overlap

## Implementation Notes for Minerva
- Read 8-byte header size first
- Parse JSON header (System.Text.Json)
- Memory-map remainder for tensor access
- Validate offsets don't overlap
- Validate buffer is fully indexed
```
- [ ] SafeTensors spec created

#### Task 3.3: Create Tokenizers spec document
Create `docs/specs/llm/Tokenizers.md`:
```markdown
# Tokenizer Format Specifications

## Overview
Minerva supports three tokenizer sources:
1. GGUF-embedded (bundled with weights)
2. Hugging Face tokenizer.json
3. SentencePiece tokenizer.model

## GGUF-Embedded Tokenizer
Stored in GGUF key-value metadata:
- `tokenizer.ggml.model` - tokenizer type (llama, gpt2, etc.)
- `tokenizer.ggml.tokens` - array of token strings
- `tokenizer.ggml.scores` - array of token scores (for SentencePiece)
- `tokenizer.ggml.merges` - BPE merge rules
- `tokenizer.ggml.bos_token_id` - beginning of sequence
- `tokenizer.ggml.eos_token_id` - end of sequence

## Hugging Face tokenizer.json
JSON format with components:
```json
{
  "version": "1.0",
  "model": {
    "type": "BPE",
    "vocab": { "token": id, ... },
    "merges": ["a b", "ab c", ...]
  },
  "pre_tokenizer": { ... },
  "post_processor": { ... },
  "decoder": { ... }
}
```

## SentencePiece tokenizer.model
Protocol buffer format.
Use SentencePiece library or parse protobuf directly.

Key fields:
- trainer_spec - training parameters
- normalizer_spec - text normalization
- pieces - vocabulary with scores

## Implementation Notes for Minerva
- Prefer GGUF-embedded when available (single file)
- Fall back to tokenizer.json for HF models
- SentencePiece requires protobuf parsing
- All tokenizers produce int[] token IDs
```
- [ ] Tokenizers spec created

#### Task 3.4: Create Minerva ADR-001
Create `docs/adrs/minerva/ADR-001-weight-formats.md`:
```markdown
# ADR-001: Minerva Weight and Tokenizer Format Support

## Status
Proposed

## Context
Minerva is a zero-GC, .NET-native tensor inference substrate. To load pre-trained models, Minerva must parse industry-standard weight and tokenizer formats. We follow the same philosophy as Mercury: 100% spec compliance, memory-mapped where possible, zero external dependencies beyond BCL.

## Decision

### Weight Formats (Priority Order)

1. **GGUF** - Primary format
   - Spec: docs/specs/llm/GGUF.md
   - Self-contained (weights + tokenizer + metadata)
   - Memory-mappable tensor data
   - Quantization support (Q4, Q8, etc.)

2. **SafeTensors** - Secondary format
   - Spec: docs/specs/llm/SafeTensors.md
   - 8-byte header size + JSON header + contiguous tensor data
   - Little-endian, row-major
   - Memory-mappable

### Tokenizer Formats

1. **GGUF-embedded** - Bundled with GGUF weights
2. **tokenizer.json** - Hugging Face format (JSON)
3. **tokenizer.model** - SentencePiece (protobuf)

### Implementation Approach

- Memory-mapped file access via `System.IO.MemoryMappedFiles`
- Zero-copy tensor views using `Span<T>` / `Memory<T>`
- No GC allocations in hot paths
- BCL-only dependencies

## Consequences

- Full ecosystem compatibility (Ollama, LM Studio, HuggingFace models)
- Sovereign infrastructure - no llama.cpp, no GGML, no Python
- Same architectural patterns as Mercury

## Implementation Tasks

- [ ] GgufReader: Header parsing
- [ ] GgufReader: KV metadata parsing
- [ ] GgufReader: Tensor info parsing
- [ ] GgufReader: Memory-mapped tensor access
- [ ] SafeTensorsReader: Header parsing
- [ ] SafeTensorsReader: JSON metadata parsing
- [ ] SafeTensorsReader: Memory-mapped tensor access
- [ ] Tokenizer: GGUF-embedded extraction
- [ ] Tokenizer: tokenizer.json parsing
- [ ] Tokenizer: BPE encode/decode
```
- [ ] Minerva ADR-001 created

### Phase 4: Update CLAUDE.md

#### Task 4.1: Update CLAUDE.md paths
Update CLAUDE.md to reflect new documentation structure:
- Change `docs/mercury-adr-*.md` references to `docs/adrs/mercury/`
- Add Minerva section
- Update solution structure diagram

Key changes:
```markdown
## In-Flight Work: ADRs

Architecture Decision Records track planning and progress for complex features:

```bash
ls docs/adrs/mercury/   # Mercury ADRs
ls docs/adrs/minerva/   # Minerva ADRs
```

## Solution Structure

```
SkyOmega.sln
├── docs/
│   ├── adrs/              # Architecture Decision Records
│   │   ├── mercury/       # Mercury-specific ADRs
│   │   └── minerva/       # Minerva-specific ADRs
│   ├── specs/             # External format specifications
│   │   ├── rdf/           # RDF specs (SPARQL, Turtle, etc.)
│   │   └── llm/           # LLM specs (GGUF, SafeTensors, etc.)
│   ├── architecture/      # Conceptual documentation
│   └── api/               # API documentation
├── src/
│   ├── Mercury/           # Knowledge substrate - RDF storage and SPARQL
│   ├── Mercury.*/         # Mercury components
│   ├── Minerva/           # Thought substrate - tensor inference
│   └── Minerva.*/         # Minerva components
├── tests/
├── benchmarks/
└── examples/
```
```
- [ ] CLAUDE.md updated

#### Task 4.2: Update cross-references in moved files
After moving files, update internal links that reference old paths:
```bash
# Find and fix references to old ADR paths
grep -rl "mercury-adr-" docs/architecture/ docs/api/ CLAUDE.md | xargs sed -i '' 's|docs/mercury-adr-|docs/adrs/mercury/ADR-|g'

# Fix references to moved architecture docs
grep -rl "docs/sky-omega-convergence.md" . | xargs sed -i '' 's|docs/sky-omega-convergence.md|docs/architecture/sky-omega-convergence.md|g'
grep -rl "docs/api-usage.md" CLAUDE.md | xargs sed -i '' 's|docs/api-usage.md|docs/api/api-usage.md|g'
```
Manual review needed for:
- CLAUDE.md SERVICE clause reference: `docs/mercury-adr-service-scan-interface.md` → `docs/adrs/mercury/ADR-006-service-scan-interface.md`
- [ ] Cross-references updated

### Phase 5: Solution File Update

#### Task 5.1: Add Minerva projects to solution
Add to SkyOmega.sln:
- Minerva project
- Minerva solution folder
- Nest Minerva under appropriate folder

```bash
dotnet sln add src/Minerva/Minerva.csproj
```
- [ ] Solution file updated

## Verification

After all tasks complete:

```bash
# Verify directory structure
find docs -type d | head -20
find src -type d | head -30

# Verify scratch directories moved (should not exist at root)
[ -d reasoning ] && echo "ERROR: reasoning/ still at root" || echo "OK: reasoning/ moved"
[ -d semantic ] && echo "ERROR: semantic/ still at root" || echo "OK: semantic/ moved"

# Verify solution builds
dotnet build SkyOmega.sln

# Verify no broken links in CLAUDE.md
grep -oE '\[.*\]\(docs/[^)]+\)' CLAUDE.md | while read link; do
  path=$(echo "$link" | sed 's/.*(\(.*\))/\1/')
  [ -e "$path" ] || echo "BROKEN: $path"
done
```

## Rollback

If needed, revert via git:
```bash
git checkout -- .
```

## Notes

- This restructure is additive - no existing code is modified
- Mercury functionality unchanged
- Minerva starts as scaffold for future implementation
- All moves use `git mv` to preserve history
- Root-level `reasoning/` and `semantic/` directories are scratch/exploratory content, consolidated under `docs/scratches/`
- `docs/specs/rdf/` reserved for future W3C spec summaries (SPARQL, Turtle, etc.)