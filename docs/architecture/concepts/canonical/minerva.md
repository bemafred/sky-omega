# Minerva

## Canonical Definition

Minerva is the inference **substrate** of Sky Omega.

Minerva is responsible for **tokenization**, **tensor operations**, **model execution**, and **hardware acceleration**.
She loads weights, encodes text, executes forward passes, and routes computation to available hardware—CPU, GPU, or accelerator.

Minerva does not orchestrate behavior and does not store knowledge.
Instead, she operates over **weights**, **tokens**, **tensors**, and **compute backends**, providing concrete capabilities such as:

- Weight loading (GGUF, SafeTensors)
- Tokenization (BPE, SentencePiece)
- Matrix operations and attention
- Direct hardware access (SIMD, Metal, CUDA)

Minerva makes inference **local and sovereign**, rather than dependent on external services.

---

## Non-Goals

- Minerva is not a language model; she executes them.
- Minerva is not a training or fine-tuning framework.
- Minerva does not wrap external inference libraries.
- Minerva does not impose ML framework opinions; she goes direct to hardware.

---

## Notes

- Minerva acts as the engine beneath Sky's generative capabilities.
- Her role aligns with the Roman goddess of wisdom: patient craft, strategic execution.
- Minerva enables sovereignty by owning the full inference path from weights to output.