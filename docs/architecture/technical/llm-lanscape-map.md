# LLM Landscape Map — Sky Omega / Minerva Planning

### February 2026 — Reconnaissance for LLM Pool Architecture

——

## 1. Scale of the Open-Weight Ecosystem

The numbers are staggering compared to even 12 months ago:

- **Hugging Face**: 500,000+ models on the platform (not all LLMs — includes embeddings, vision, etc.)
- **Distinct LLM model families with downloadable weights**: ~30-40 significant families
- **With quantized variants, fine-tunes, and community derivatives**: Thousands
- **Ollama library** (ready-to-run GGUF): 497+ models from 133+ providers (per llmfit’s count)

The important architectural insight: the number of *model families* that matter is manageable (~20-30), but each has multiple size variants (1B to 685B parameters) and quantization levels (Q2_K through Q8_0). For the LLM pool concept, **model family × size × quantization = the configuration space**.

——

## 2. The Major Open-Weight Model Families (Feb 2026)

### Tier 1 — Frontier-class (competing with GPT-5/Claude)

|Family             |Provider           |Params                |Architecture         |License                                               |Notes                                             |
|-——————|-——————|-———————|———————|——————————————————|—————————————————|
|**DeepSeek V3.2**  |DeepSeek (China)   |685B (MoE)            |Sparse attention     |MIT (code) / DeepSeek License (weights, free <$1M rev)|Speciale variant matches GPT-5. 128K context.     |
|**GLM-5 / GLM-4.7**|Zhipu AI (China)   |Various               |Dense                |Open license                                          |#1 on open-source rankings Feb 2026. 200K context.|
|**Qwen3-235B**     |Alibaba (China)    |235B (22B active, MoE)|128 experts, 8 active|Apache 2.0                                            |Strong multilingual. 1M+ context in latest.       |
|**Kimi K2.5**      |Moonshot AI (China)|Large MoE             |MoE                  |Open                                                  |96% AIME 2025. 256K context.                      |
|**GPT-OSS-120B**   |OpenAI (US)        |117B                  |Dense                |Open-weight                                           |Chain-of-thought access. Single-GPU deployment.   |

### Tier 2 — Strong general purpose (runnable on serious consumer hardware)

|Family                      |Provider           |Sizes        |License                |Notes                                                                    |
|-—————————|-——————|-————|————————|-————————————————————————|
|**Llama 4** (Scout/Maverick)|Meta               |Various (MoE)|Llama Community License|Massive ecosystem. 10M context (Scout). Commercial restrictions at scale.|
|**Llama 3.3**               |Meta               |70B          |Llama Community License|Still widely used. Strong quality.                                       |
|**Mistral Small 3**         |Mistral AI (France)|24B          |Apache 2.0             |Excellent speed/quality ratio. Fully open.                               |
|**Mistral Large**           |Mistral AI         |Large        |Restricted             |Near GPT-4 class.                                                        |
|**Phi-4**                   |Microsoft          |16B          |MIT                    |Punches above weight on reasoning. Synthetic data trained.               |
|**Gemma 3**                 |Google             |2B-27B       |Gemma License          |Good for consumer GPUs via QAT.                                          |
|**DeepSeek-Coder-V2**       |DeepSeek           |Specialized  |DeepSeek License       |300+ programming languages.                                              |
|**MiMo-V2-Flash**           |Various            |Mid-size     |Open                   |87% LiveCodeBench.                                                       |

### Tier 3 — Specialized / Smaller / Nordic

|Family               |Provider          |Sizes   |License        |Notes                                                                                |
|———————|——————|———|—————|-————————————————————————————|
|**GPT-SW3**          |AI Sweden         |126M-40B|Permissive open|First Nordic LLM. Swedish, Norwegian, Danish, Icelandic, English. Older architecture.|
|**Viking**           |Silo AI / TurkuNLP|7B-33B  |Apache 2.0     |Nordic languages + English + code. Trained on LUMI. Best-in-class Nordic.            |
|**StarCoder 2**      |BigCode/HF        |15B     |OpenRAIL-M     |80+ programming languages.                                                           |
|**Cohere Command R+**|Cohere            |104B    |Restricted     |Enterprise RAG focus.                                                                |

——

## 3. Swedish Language Suitability

### Dedicated Nordic models

- **GPT-SW3** (AI Sweden): Purpose-built for Swedish. Sizes up to 40B. Trained on Swedish, Norwegian, Danish, Icelandic, English. Instruction-tuned variants available. Older (2023) but foundational.
- **Viking** (Silo AI): Covers all Nordic languages + English + code. Apache 2.0. Trained on 2T tokens. Best Nordic performance without English compromise. 7B-33B.
- **OpenEuroLLM**: EU-funded consortium (started Feb 2025) building multilingual European models. AI Sweden is one of 20 partners. Future deliverables.

### Major models with Swedish capability

- **Qwen3**: Strong multilingual. Chinese and English primary, but designed for broad language coverage. Likely decent Swedish.
- **Llama 4**: Multilingual training. Swedish usable but not primary focus.
- **DeepSeek V3.2**: English and Chinese primary. Other languages “usable but noticeably weaker.”
- **Mistral**: French company, some European language attention, but no dedicated Swedish training.

### Assessment for Sky Omega

Swedish support splits into two tiers:

1. **Dedicated Nordic models** (GPT-SW3, Viking) — good for Swedish-specific tasks but smaller/older
1. **Frontier multilingual** (Qwen3, Llama 4) — better general reasoning but weaker Swedish

**The pool architecture is exactly right here** — use Nordic-specialized models for Swedish text processing and frontier models for reasoning/code. James can route based on task characteristics.

——

## 4. Programming Language Support

Most frontier models handle code well. Standouts:

|Model                 |Code Strength      |Notes                 |
|-———————|-——————|-———————|
|DeepSeek-Coder-V2     |300+ languages     |Purpose-built         |
|DeepSeek V3.2 Speciale|90% LiveCodeBench  |Top benchmark         |
|GLM-4.7               |89% LiveCodeBench  |Strong coding variant |
|GPT-OSS-120B          |Strong             |OpenAI’s open entry   |
|Qwen3-Coder           |Specialized variant|Dedicated coding model|
|StarCoder 2           |80+ languages      |Code-only             |
|Llama 3.3 70B         |Strong general     |Widely validated      |

C# specifically: All frontier models handle C# well. DeepSeek-Coder-V2 and the general frontier models will understand .NET patterns, LINQ, async/await etc.

——

## 5. Hardware → Model Size Mapping

|Hardware                 |Max Model (quantized)|Examples                                 |
|-————————|———————|——————————————|
|Pi 5 (8GB)               |~3-4B Q4             |Phi-3-mini, Gemma-2B                     |
|16GB RAM (CPU)           |~7-8B Q4             |Mistral 7B, Llama 3 8B                   |
|24GB VRAM (RTX 3090/4090)|~40B Q4              |Qwen 32B, Llama 3.3 70B (Q3)             |
|48GB VRAM (dual GPU)     |~70B Q4              |Llama 3.3 70B, Qwen 72B                  |
|80GB VRAM (A100/H100)    |~70B Q8 or 120B Q4   |GPT-OSS-120B                             |
|Multi-GPU cluster        |Frontier MoE         |DeepSeek V3.2 (needs 350GB+ VRAM even Q4)|

**For the Pi 5 cluster**: Each node can run small models (3-4B). The interesting architectural question for Mercury-distributed-on-Pi-5 is whether small local models + remote frontier API creates a useful topology.

——

## 6. API Standardization — The Current State

### The OpenAI API has become the de facto standard

This is the single most important finding for your pool architecture:

**The OpenAI chat/completions API format has won.** Nearly every provider and local inference engine now implements OpenAI-compatible endpoints:

- **Local inference engines**: Ollama, vLLM, SGLang, LM Studio, llama.cpp server — all expose OpenAI-compatible APIs
- **Cloud providers with OpenAI-compatible APIs**: Together.ai, Groq, Fireworks.ai, SiliconFlow, Inference.net
- **Gateways that normalize to OpenAI format**: LiteLLM (100+ providers), Bifrost, Helicone, Cloudflare AI Gateway

**Exception: Anthropic** uses its own Messages API format (different auth headers, different JSON structure, different role semantics). This is a deliberate choice. However, Ollama recently announced Anthropic API compatibility, and the Open Responses specification from OpenAI is gaining traction as a second standard for agentic workflows.

### What this means for the pool architecture

A pool that speaks OpenAI chat/completions format can connect to:

- Any local model via Ollama/vLLM/llama.cpp
- Most cloud providers directly
- Anthropic via a thin translation layer (or LiteLLM)

**Recommended**: Target OpenAI chat/completions as the primary wire protocol. Add an Anthropic adapter. This gives you ~95% coverage with two protocol implementations.

——

## 7. The .NET SDK Situation (Critical for Sky Omega)

### OpenAI .NET SDK

- **Stable since October 2024** (NuGet: `OpenAI`). Full REST API support.
- Significant improvement since your summer 2025 SkyChatBot MVP experience — the beta churn has settled.
- Supports GPT-5, o-series, Assistants v2, chat completions, embeddings.
- Azure variant: `Azure.AI.OpenAI` tracks alongside, currently on 2025-11-01 preview API version.

### Microsoft.Extensions.AI (MEAI)

- **Reached GA at Build 2025** with production-ready abstractions.
- Provides `IChatClient` interface — the .NET abstraction for LLM interaction.
- Supports: Azure OpenAI, OpenAI, Ollama.
- **Does NOT yet natively support Anthropic Claude** (as of late 2025). Workarounds exist via DelegatingHandler shims.
- Community package `Microsoft.Extensions.AI.Anthropic` exists (embeds Anthropic SDK due to bugs in official package).
- Anthropic has released a C# SDK (`Anthropic` NuGet) that implements `IChatClient`.

### Microsoft Agent Framework

- Announced October 2025, unifying Semantic Kernel + AutoGen.
- Supports OpenAI, Azure OpenAI, Anthropic Claude, Ollama.
- MCP C# SDK in preview (November 2025).
- Likely overkill for Sky Omega — you have James.

### .NET 10 (LTS, November 2025)

- Built-in AI integration. “Microsoft Agent Framework” is the flagship.
- C# 14 features.
- Visual Studio 2026 with deep Copilot integration.

### Assessment for Sky Omega

The .NET LLM SDK landscape has **dramatically improved** since summer 2025:

1. **OpenAI SDK is stable** — no longer the moving target you experienced
1. **MEAI provides the abstraction layer** — `IChatClient` is the interface to target
1. **Ollama support in MEAI** — means local models work through the same interface
1. **Anthropic is the remaining rough edge** — functional but requires shims or community packages

**However** — for Minerva’s zero-dependency philosophy, you probably don’t want Microsoft.Extensions.AI at all. You want your own thin HTTP client that speaks OpenAI chat/completions JSON directly. The protocol is simple enough:

```
POST /v1/chat/completions
{
  “model”: “...”,
  “messages”: [{“role”: “user”, “content”: “...”}],
  “temperature”: 0.7,
  “stream”: true
}
```

This is ~50 lines of C# with HttpClient + System.Text.Json. No NuGet packages needed. Full semantic sovereignty.

——

## 8. Weight Formats

|Format                |Used By                     |Notes                                                   |
|-———————|-—————————|———————————————————|
|**GGUF**              |llama.cpp, Ollama, LM Studio|Most common for local inference. Quantized. Single file.|
|**SafeTensors**       |Hugging Face, vLLM, SGLang  |Standard for full-precision/research. Multiple shards.  |
|**PyTorch (.bin/.pt)**|Legacy HF models            |Being replaced by SafeTensors.                          |
|**GGML**              |Deprecated                  |Predecessor to GGUF.                                    |

**For Minerva**: You already have GGUF + SafeTensors in the skeleton ADR-001. This covers the two formats that matter.

——

## 9. LLM Pool Architecture — Observations

Given everything above, the pool concept maps well to the landscape:

### Local pool members

- **Small models** (1-8B): Fast, on-device, good for classification/routing/embeddings
- **Medium models** (8-32B): Good general capability, fits consumer GPU
- **Specialized**: Code models, Swedish models, embedding models

### Remote pool members

- **OpenAI API** (GPT-5 family): Strongest reasoning, expensive
- **Anthropic API** (Claude family): Strong coding, different failure modes
- **DeepSeek API**: Frontier quality, aggressive pricing ($0.07-0.42/M tokens)
- **Groq/Together.ai/Fireworks**: Fast inference of open models, pay-per-token

### Pool properties that emerge

- **Redundancy**: Multiple models = no single point of failure
- **A/B testing**: Same prompt to different models, compare results
- **Cost optimization**: Route simple tasks to small/cheap, complex to frontier
- **Sovereignty tiers**: Local-only for sensitive, remote-OK for general
- **Language routing**: Swedish queries → Nordic models, reasoning → frontier
- **Laboratory properties**: Exactly what you said — this becomes an experimental platform

### Suggested pool taxonomy

```
Pool
├── Local
│   ├── General (Mistral 7B, Phi-4, Llama 8B)
│   ├── Code (DeepSeek-Coder, StarCoder)
│   ├── Nordic (GPT-SW3, Viking)
│   └── Embedding (all-MiniLM, mxbai-embed)
├── Remote
│   ├── OpenAI (GPT-5 family)
│   ├── Anthropic (Claude family)
│   ├── DeepSeek (V3.2 API)
│   └── Open-model hosts (Groq, Together, Fireworks)
└── Minerva (future local inference substrate)
```

——

## 10. Open Questions for Architecture

1. **Pool protocol**: OpenAI chat/completions as universal wire format + Anthropic adapter? Or abstract higher?
1. **Model discovery**: Static configuration vs. runtime probing (llmfit-style)?
1. **Routing intelligence**: Where does James decide which pool member handles a request? Rules? Learned?
1. **Result merging**: When do you query multiple pool members and synthesize?
1. **Streaming**: SSE (OpenAI style) vs. Anthropic’s streaming format — normalize at pool level?
1. **Context management**: Each pool member has different context limits — Lucy needs to track this per member.
1. **Cost tracking**: Tokens used per provider, for both operational and experimental awareness.
1. **Minerva transition**: As Minerva matures, local pool members migrate from Ollama to native Minerva inference.

——

## Sources & References

- Hugging Face model hub: huggingface.co/models
- Ollama library: ollama.com/library
- llmfit: github.com/AlexsJones/llmfit
- AI Sweden GPT-SW3: huggingface.co/AI-Sweden-Models
- Viking: silo.ai
- OpenAI .NET SDK: NuGet `OpenAI`
- Microsoft.Extensions.AI: NuGet `Microsoft.Extensions.AI`
- LiteLLM: github.com/BerriAI/litellm
- OpenEuroLLM: EU consortium, started Feb 2025
- WhatLLM rankings: whatllm.org

——

*Document generated February 27, 2026. The LLM landscape moves fast — validate specific model versions and benchmarks before architectural commitments.*