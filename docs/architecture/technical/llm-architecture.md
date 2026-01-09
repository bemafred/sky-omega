# LLM Architecture — How It Actually Works  
*A low-friction, systems-level view*

This document summarizes the core architectural concepts behind modern Large Language Models (LLMs), focusing on **mechanics, data flow, memory, and hardware alignment** rather than hype or metaphor. It reflects a **low-friction design philosophy**: minimize architectural impedance, maximize residency, reuse, and determinism.

—-

## 1. The End-to-End LLM Pipeline (Mechanical View)

Text
→ Tokenizer
→ Token IDs
→ Embedding Lookup
→ Transformer Layers
→ Logits
→ Token Selection
→ Token IDs
→ Detokenization
→ Text

Everything between *token-in* and *token-out* is **just math**.

No strings.  
No grammar.  
No semantics.  

Only tensors, linear algebra, and probability.

——

## 2. Tokenization: Statistics, Not Language

### What a tokenizer is
A tokenizer is:
- a **precomputed vocabulary** of UTF-8 spans
- plus a **deterministic longest-match algorithm**

It converts text into **integer token IDs**.

### How the vocabulary is built
- Trained offline on **massive multilingual corpora**
- Collects **frequency statistics of byte/character sequences**
- Common sequences become tokens
- Rare sequences fragment

Biases are baked in:
- English dominates
- Programming syntax is overrepresented
- Formal languages (RDF, JSON, etc.) tokenize poorly

### Key properties
- Tokenizers are **lossless**
- Deterministic
- Fixed at runtime
- Define the *atomic units the model can think in*

—-

## 3. Tokens, Context, and KV-Cache

### Token flow
- Tokens enter the model **sequentially**, one by one
- The **context window** is the ordered list of tokens seen so far

### KV-cache
For each token and each layer:
- Keys (K) and Values (V) are computed **once**
- Stored in the **KV-cache**
- Reused by all future tokens

This avoids recomputation and makes inference viable.

**KV-cache characteristics**
- Grows with sequence length
- Read-heavy, append-only
- Numerically sensitive
- Typically stored in **BF16 / FP16**

KV-cache is often the **dominant memory consumer**.

—-

## 4. Tensors: The Universal Data Structure

### What a tensor is
A tensor is an **N-dimensional array of numbers with a defined shape**.

Special cases:
- Scalar → rank-0 tensor
- Vector → rank-1
- Matrix → rank-2
- Rank-3 → volume (stack of matrices)
- Rank-4+ → higher-order structures (e.g. KV-cache)

In memory:
- All tensors are **flat arrays**
- Shape + strides give structure

### Why tensors dominate
Tensors:
- Are regular and predictable
- Map perfectly to SIMD, SIMT, and matrix hardware
- Enable massive parallelism
- Collapse nested loops into vectorized operations

LLMs are tensor machines because **modern hardware is tensor hardware**.

—-

## 5. Precision, Quantization, and Numeric Strategy

### Floating point formats
- FP32: precise, slow, expensive (training/debug)
- FP16: higher precision, smaller exponent, less stable
- **BF16**: same exponent as FP32, fewer mantissa bits  
  → **stable and ideal for inference**

### Quantization
Weights are often stored as:
- **INT8** or **INT4**
- With scale factors

Typical inference mix:
- Weights: INT4 / INT8
- Activations: BF16
- KV-cache: BF16
- Accumulation: BF16 / FP16

Quantization is **irreversible** and a deliberate tradeoff:
- Precision ↓
- Throughput ↑
- Memory ↓

### CPU vs GPU
- **CPU inference** prefers INT8 + FP16/BF16
- INT4 is usually inefficient on CPUs
- GPUs excel at INT4/INT8 mixed precision

——

## 6. Unified Memory and Architectural Friction

### Discrete memory model (PC)
- System RAM ≠ GPU VRAM
- Explicit copies
- Staging buffers
- Memory duplication
- Idle VRAM outside AI workloads

This resembles **segmented memory / far pointers** from earlier eras:
- Hardware can do it
- Programmer pays the cognitive tax

### Unified memory model (Apple Silicon)
- One physical memory pool
- CPU, GPU, accelerators share it
- Zero-copy by default
- KV-cache and weights stay resident
- mmap works naturally

Unified memory is effectively **flat addressing for AI**.

—-

## 7. Hardware Domains and Their Roles

### Training at scale
- Dominated by **NVIDIA**
- Reasons:
  - HBM
  - NVLink / NVSwitch
  - CUDA ecosystem
  - End-to-end stack control

Training is **coordination-bound**, not compute-bound.

### API inference at scale
- Also NVIDIA-dominated (today)
- Due to:
  - Ecosystem inertia
  - Tooling maturity
  - Large memory GPUs

Dominance here is economic and software-driven, not inevitable.

### Local inference
- Unified-memory systems shine
- Memory-bound, KV-heavy workloads
- Long-lived sessions
- Deterministic latency

**For local, persistent, memory-centric cognitive systems, unified memory is the least hostile substrate.**

—-

## 8. Low-Friction as an Architectural Invariant

Low friction means:
- Fewer memory domains
- Fewer copies
- Fewer special cases
- Fewer lifetimes to manage
- Fewer architectural leaks

Historically, low-friction architectures win:
- Flat memory over segmented
- Virtual memory over overlays
- SIMD over scalar loops
- Unified memory over staged accelerators

LLMs amplify this effect because they are:
- Memory-resident
- Append-only (KV)
- Tensor-heavy
- Long-lived

——

## 9. Additional Important Concepts (Not Explicitly Covered Earlier)

### Sampling vs determinism
- The model outputs **logits**
- Sampling policy (temperature, top-k, top-p) is external
- Determinism is a runtime choice, not a model property

### Attention scaling
- Quadratic cost with context length
- KV-cache reduces compute but not memory
- Sliding windows, eviction, and prefix caching matter

### Residency vs throughput
- Many systems optimize for peak throughput
- Cognitive systems optimize for **residency and continuity**
- Different architectural priorities

### Architecture > model size
- Smaller models + good architecture often beat larger models + friction
- Memory layout, reuse, and orchestration matter as much as parameters

—-

## 10. One-Page Mental Model

- Tokenizer: statistical UTF-8 span lookup
- Tokens: integers
- Embeddings: vectors
- Model: tensor transformations
- KV-cache: append-only memory of thought
- Hardware: moves tensors efficiently or gets in the way
- Architecture: determines friction
- Low friction: compounds over time

—-

## Closing Statement

LLMs are not mysterious.  
They are **memory-resident, tensor-driven, numerically approximate machines** whose real constraints are **data movement, memory topology, and architectural friction**.

Understanding and minimizing friction is the difference between:

- a clever demo
- and a durable cognitive system