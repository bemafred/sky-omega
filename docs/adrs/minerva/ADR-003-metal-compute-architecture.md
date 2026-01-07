# ADR-003: Metal Compute Shader Architecture

## Status

Proposed

## Related ADRs

- [ADR-001: Weight and Tokenizer Format Support](ADR-001-weight-formats.md) - Model loading foundation
- [ADR-002: Hardware Acceleration Strategy](ADR-002-hardware-acceleration-strategy.md) - Backend abstraction

## Context

ADR-002 established the multi-backend architecture with Apple Silicon support via Accelerate (AMX) and Metal (GPU). This ADR specifies the Metal compute shader architecture for transformer inference on M4 Max and similar Apple Silicon hardware.

### Target Hardware: M4 Max

| Resource            | Specification | Implication                       |
|---------------------|---------------|-----------------------------------|
| GPU Cores           | 40            | Massive parallelism for attention |
| Unified Memory      | 128 GB        | Full model + KV-cache in-memory   |
| Memory Bandwidth    | 546 GB/s      | Memory-bound ops bottleneck here  |
| Threadgroup Memory  | 32 KB         | Constrains tile sizes             |
| SIMD Width          | 32 threads    | Natural tile dimension            |

### Transformer Inference Profile

| Operation        | Characteristic | Optimal Backend             |
|------------------|---------------|------------------------------|
| Embedding Lookup | Memory-bound  | Metal (coalesced reads)      |
| RMSNorm          | Memory-bound  | Metal (elementwise)          |
| QKV Projection   | Compute-bound | AMX via Accelerate           |
| RoPE             | Compute-bound | Metal (parallel)             |
| Attention (QK^T) | Memory-bound  | Metal (Flash Attention)      |
| Softmax          | Memory-bound  | Metal (fused with attention) |
| FFN MatMul       | Compute-bound | AMX via Accelerate           |
| SiLU/GELU        | Compute-bound | Metal (elementwise)          |

**Decision principle**: Large matrix multiplications → AMX. Everything else → Metal GPU.

## Decision

### Shader Organization

```
minerva-native/
└── apple/
    ├── minerva_apple.m              # Objective-C bridge for P/Invoke
    ├── shaders/
    │   ├── attention.metal          # Flash Attention variants
    │   ├── elementwise.metal        # SiLU, GELU, RMSNorm, RoPE
    │   ├── quantized_matmul.metal   # Q4/Q8 dequantize + matmul
    │   ├── softmax.metal            # Numerically stable softmax
    │   └── embedding.metal          # Token embedding lookup
    ├── minerva.metallib             # Compiled shader library
    └── build.sh                     # xcrun metal compilation
```

### Core Shader Interfaces

#### 1. Flash Attention

Standard attention has O(n²) memory complexity for the attention matrix. Flash Attention (Dao et al., 2022) tiles the computation to achieve O(n) memory with equivalent output.

```metal
// attention.metal

#include <metal_stdlib>
using namespace metal;

struct AttentionParams {
    uint seq_q;          // Query sequence length
    uint seq_kv;         // Key/Value sequence length  
    uint head_dim;       // Dimension per head (typically 64-128)
    uint num_heads;      // Number of attention heads
    float scale;         // 1/sqrt(head_dim)
    uint kv_cache_offset; // For incremental decoding
};

/// Flash Attention forward pass with online softmax
/// Processes attention in tiles to avoid O(n²) memory allocation
///
/// Memory layout (all row-major):
///   Q: [batch, num_heads, seq_q, head_dim]
///   K: [batch, num_heads, seq_kv, head_dim]  
///   V: [batch, num_heads, seq_kv, head_dim]
///   O: [batch, num_heads, seq_q, head_dim]
kernel void flash_attention_forward(
    device const half* Q [[buffer(0)]],
    device const half* K [[buffer(1)]],
    device const half* V [[buffer(2)]],
    device half* O [[buffer(3)]],
    constant AttentionParams& params [[buffer(4)]],
    
    threadgroup half* shared [[threadgroup(0)]],
    
    uint3 tid [[thread_position_in_threadgroup]],
    uint3 tgid [[threadgroup_position_in_grid]],
    uint simd_lane [[thread_index_in_simdgroup]])
{
    const uint TILE_Q = 64;   // Query tile size
    const uint TILE_KV = 64;  // Key/Value tile size
    
    // Partition shared memory
    // Layout: [Q_tile | K_tile | V_tile | S_tile (float)]
    threadgroup half* Q_tile = shared;
    threadgroup half* K_tile = shared + TILE_Q * params.head_dim;
    threadgroup half* V_tile = K_tile + TILE_KV * params.head_dim;
    threadgroup float* S_tile = (threadgroup float*)(V_tile + TILE_KV * params.head_dim);
    
    uint batch_idx = tgid.z;
    uint head_idx = tgid.y;
    uint q_tile_idx = tgid.x;
    uint q_start = q_tile_idx * TILE_Q;
    
    // Base pointers for this batch/head
    uint batch_head_offset = (batch_idx * params.num_heads + head_idx) * 
                             params.seq_kv * params.head_dim;
    device const half* Q_head = Q + batch_head_offset;
    device const half* K_head = K + batch_head_offset;
    device const half* V_head = V + batch_head_offset;
    device half* O_head = O + batch_head_offset;
    
    // Online softmax accumulators (per-thread, in registers)
    float m_i = -INFINITY;  // Running max
    float l_i = 0.0f;       // Running sum of exp(x - max)
    float O_acc[128];       // Accumulator (head_dim ≤ 128)
    
    for (uint d = 0; d < params.head_dim; d++) {
        O_acc[d] = 0.0f;
    }
    
    // Load Q tile (cooperative)
    for (uint i = tid.x; i < TILE_Q * params.head_dim; i += 256) {
        uint q_idx = q_start + i / params.head_dim;
        uint d_idx = i % params.head_dim;
        Q_tile[i] = (q_idx < params.seq_q) ? Q_head[q_idx * params.head_dim + d_idx] : half(0);
    }
    threadgroup_barrier(mem_flags::mem_threadgroup);
    
    // Iterate over KV tiles
    for (uint kv_start = 0; kv_start < params.seq_kv; kv_start += TILE_KV) {
        
        // Load K, V tiles (cooperative)
        for (uint i = tid.x; i < TILE_KV * params.head_dim; i += 256) {
            uint kv_idx = kv_start + i / params.head_dim;
            uint d_idx = i % params.head_dim;
            half k_val = (kv_idx < params.seq_kv) ? K_head[kv_idx * params.head_dim + d_idx] : half(0);
            half v_val = (kv_idx < params.seq_kv) ? V_head[kv_idx * params.head_dim + d_idx] : half(0);
            K_tile[i] = k_val;
            V_tile[i] = v_val;
        }
        threadgroup_barrier(mem_flags::mem_threadgroup);
        
        // Compute S = Q @ K^T for this tile
        uint local_q = tid.x / TILE_KV;  // Which Q row this thread handles
        uint local_k = tid.x % TILE_KV;  // Which K row this thread computes against
        
        if (local_q < TILE_Q && q_start + local_q < params.seq_q) {
            float dot = 0.0f;
            for (uint d = 0; d < params.head_dim; d++) {
                dot += float(Q_tile[local_q * params.head_dim + d]) *
                       float(K_tile[local_k * params.head_dim + d]);
            }
            dot *= params.scale;
            
            // Causal mask: only attend to positions ≤ current
            uint global_q = q_start + local_q;
            uint global_k = kv_start + local_k;
            if (global_k > global_q) {
                dot = -INFINITY;
            }
            
            S_tile[local_q * TILE_KV + local_k] = dot;
        }
        threadgroup_barrier(mem_flags::mem_threadgroup);
        
        // Online softmax update for each Q position this thread owns
        if (local_q < TILE_Q && q_start + local_q < params.seq_q) {
            // Find max in this tile’s row
            float m_tile = -INFINITY;
            for (uint j = 0; j < TILE_KV && kv_start + j < params.seq_kv; j++) {
                m_tile = max(m_tile, S_tile[local_q * TILE_KV + j]);
            }
            
            // New global max
            float m_new = max(m_i, m_tile);
            
            // Compute sum of exp for this tile
            float l_tile = 0.0f;
            for (uint j = 0; j < TILE_KV && kv_start + j < params.seq_kv; j++) {
                l_tile += exp(S_tile[local_q * TILE_KV + j] - m_new);
            }
            
            // Rescale previous accumulator
            float scale_prev = exp(m_i - m_new);
            float l_new = scale_prev * l_i + l_tile;
            
            for (uint d = 0; d < params.head_dim; d++) {
                O_acc[d] *= scale_prev * l_i / l_new;
            }
            
            // Add contribution from this KV tile
            for (uint j = 0; j < TILE_KV && kv_start + j < params.seq_kv; j++) {
                float att_weight = exp(S_tile[local_q * TILE_KV + j] - m_new) / l_new;
                for (uint d = 0; d < params.head_dim; d++) {
                    O_acc[d] += att_weight * float(V_tile[j * params.head_dim + d]);
                }
            }
            
            m_i = m_new;
            l_i = l_new;
        }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    
    // Write output
    if (tid.x < TILE_Q && q_start + tid.x < params.seq_q) {
        for (uint d = 0; d < params.head_dim; d++) {
            O_head[(q_start + tid.x) * params.head_dim + d] = half(O_acc[d]);
        }
    }
}
```

#### 2. Quantized Matrix Multiplication

GGUF models use block-quantized weights. Dequantization happens in-kernel to avoid memory expansion.

```metal
// quantized_matmul.metal

#include <metal_stdlib>
using namespace metal;

struct Q4_0_Block {
    half scale;           // Shared scale for 32 values
    uint8_t quants[16];   // 32 × 4-bit values packed into 16 bytes
};

/// Q4_0 dequantize and matrix multiply
/// A: quantized weights [M, K] in Q4_0 format
/// B: activations [K, N] in fp16
/// C: output [M, N] in fp16
kernel void matmul_q4_0(
    device const Q4_0_Block* A [[buffer(0)]],
    device const half* B [[buffer(1)]],
    device half* C [[buffer(2)]],
    constant uint& M [[buffer(3)]],
    constant uint& N [[buffer(4)]],
    constant uint& K [[buffer(5)]],
    
    uint2 gid [[thread_position_in_grid]])
{
    if (gid.x >= N || gid.y >= M) return;
    
    const uint BLOCK_SIZE = 32;
    uint num_blocks_k = K / BLOCK_SIZE;
    
    float sum = 0.0f;
    
    for (uint block = 0; block < num_blocks_k; block++) {
        uint block_idx = gid.y * num_blocks_k + block;
        Q4_0_Block blk = A[block_idx];
        float scale = float(blk.scale);
        
        uint k_base = block * BLOCK_SIZE;
        
        // Process 32 quantized values (16 bytes, 2 values per byte)
        for (uint i = 0; i < 16; i++) {
            uint8_t packed = blk.quants[i];
            
            // Low 4 bits
            int q_lo = int(packed & 0x0F) - 8;
            float a_lo = float(q_lo) * scale;
            sum += a_lo * float(B[(k_base + i * 2) * N + gid.x]);
            
            // High 4 bits  
            int q_hi = int(packed >> 4) - 8;
            float a_hi = float(q_hi) * scale;
            sum += a_hi * float(B[(k_base + i * 2 + 1) * N + gid.x]);
        }
    }
    
    C[gid.y * N + gid.x] = half(sum);
}

/// Q8_0 dequantize and matrix multiply
/// Higher precision variant for quality-sensitive layers
kernel void matmul_q8_0(
    device const half* A_scales [[buffer(0)]],
    device const int8_t* A_quants [[buffer(1)]],
    device const half* B [[buffer(2)]],
    device half* C [[buffer(3)]],
    constant uint& M [[buffer(4)]],
    constant uint& N [[buffer(5)]],
    constant uint& K [[buffer(6)]],
    
    uint2 gid [[thread_position_in_grid]])
{
    if (gid.x >= N || gid.y >= M) return;
    
    const uint BLOCK_SIZE = 32;
    uint num_blocks_k = K / BLOCK_SIZE;
    
    float sum = 0.0f;
    
    for (uint block = 0; block < num_blocks_k; block++) {
        uint block_idx = gid.y * num_blocks_k + block;
        float scale = float(A_scales[block_idx]);
        uint k_base = block * BLOCK_SIZE;
        
        for (uint i = 0; i < BLOCK_SIZE; i++) {
            int8_t q = A_quants[block_idx * BLOCK_SIZE + i];
            float a = float(q) * scale;
            sum += a * float(B[(k_base + i) * N + gid.x]);
        }
    }
    
    C[gid.y * N + gid.x] = half(sum);
}
```

#### 3. Elementwise Operations

```metal
// elementwise.metal

#include <metal_stdlib>
using namespace metal;

/// SiLU activation: x * sigmoid(x)
/// Used in Llama/Mistral FFN
kernel void silu(
    device half* x [[buffer(0)]],
    constant uint& count [[buffer(1)]],
    uint gid [[thread_position_in_grid]])
{
    if (gid >= count) return;
    
    float val = float(x[gid]);
    float sigmoid_val = 1.0f / (1.0f + exp(-val));
    x[gid] = half(val * sigmoid_val);
}

/// GELU activation (approximate)
/// Used in GPT-style models
kernel void gelu(
    device half* x [[buffer(0)]],
    constant uint& count [[buffer(1)]],
    uint gid [[thread_position_in_grid]])
{
    if (gid >= count) return;
    
    float val = float(x[gid]);
    // Approximate: 0.5 * x * (1 + tanh(sqrt(2/π) * (x + 0.044715 * x³)))
    const float SQRT_2_OVER_PI = 0.7978845608f;
    float x3 = val * val * val;
    float inner = SQRT_2_OVER_PI * (val + 0.044715f * x3);
    x[gid] = half(0.5f * val * (1.0f + tanh(inner)));
}

/// RMSNorm: x * rsqrt(mean(x²) + eps) * weight
/// Llama-style normalization
kernel void rms_norm(
    device const half* input [[buffer(0)]],
    device const half* weight [[buffer(1)]],
    device half* output [[buffer(2)]],
    constant uint& hidden_dim [[buffer(3)]],
    constant float& eps [[buffer(4)]],
    
    threadgroup float* shared [[threadgroup(0)]],
    
    uint tid [[thread_index_in_threadgroup]],
    uint tgid [[threadgroup_position_in_grid]])
{
    // Each threadgroup processes one row (one token)
    device const half* row_in = input + tgid * hidden_dim;
    device half* row_out = output + tgid * hidden_dim;
    
    // Compute sum of squares (reduction)
    float local_sum = 0.0f;
    for (uint i = tid; i < hidden_dim; i += 256) {
        float val = float(row_in[i]);
        local_sum += val * val;
    }
    shared[tid] = local_sum;
    threadgroup_barrier(mem_flags::mem_threadgroup);
    
    // Tree reduction
    for (uint stride = 128; stride > 0; stride >>= 1) {
        if (tid < stride) {
            shared[tid] += shared[tid + stride];
        }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    
    float rms = rsqrt(shared[0] / float(hidden_dim) + eps);
    
    // Normalize and apply weight
    for (uint i = tid; i < hidden_dim; i += 256) {
        float val = float(row_in[i]) * rms * float(weight[i]);
        row_out[i] = half(val);
    }
}

/// RoPE: Rotary Position Embedding
/// Applied to Q and K before attention
kernel void rope(
    device half* q [[buffer(0)]],
    device half* k [[buffer(1)]],
    constant uint& seq_len [[buffer(2)]],
    constant uint& num_heads [[buffer(3)]],
    constant uint& head_dim [[buffer(4)]],
    constant uint& position_offset [[buffer(5)]],  // For KV-cache continuation
    constant float& theta_base [[buffer(6)]],      // Usually 10000.0
    
    uint3 gid [[thread_position_in_grid]])  // [head_dim/2, num_heads, seq_len]
{
    uint half_dim = head_dim / 2;
    if (gid.x >= half_dim) return;
    
    uint pos = gid.z + position_offset;
    uint head = gid.y;
    uint d = gid.x;
    
    // Compute rotation angle
    float freq = 1.0f / pow(theta_base, float(2 * d) / float(head_dim));
    float angle = float(pos) * freq;
    float cos_val = cos(angle);
    float sin_val = sin(angle);
    
    // Index into Q and K
    uint base_idx = (gid.z * num_heads + head) * head_dim;
    uint idx_real = base_idx + d;
    uint idx_imag = base_idx + d + half_dim;
    
    // Rotate Q
    float q_real = float(q[idx_real]);
    float q_imag = float(q[idx_imag]);
    q[idx_real] = half(q_real * cos_val - q_imag * sin_val);
    q[idx_imag] = half(q_real * sin_val + q_imag * cos_val);
    
    // Rotate K
    float k_real = float(k[idx_real]);
    float k_imag = float(k[idx_imag]);
    k[idx_real] = half(k_real * cos_val - k_imag * sin_val);
    k[idx_imag] = half(k_real * sin_val + k_imag * cos_val);
}
```

#### 4. Softmax (Standalone)

```metal
// softmax.metal

#include <metal_stdlib>
using namespace metal;

/// Numerically stable softmax over rows
/// Uses two-pass algorithm: max reduction, then exp and sum
kernel void softmax(
    device half* x [[buffer(0)]],
    constant uint& rows [[buffer(1)]],
    constant uint& cols [[buffer(2)]],
    
    threadgroup float* shared [[threadgroup(0)]],
    
    uint tid [[thread_index_in_threadgroup]],
    uint tgid [[threadgroup_position_in_grid]])
{
    device half* row = x + tgid * cols;
    
    // Pass 1: Find max
    float local_max = -INFINITY;
    for (uint i = tid; i < cols; i += 256) {
        local_max = max(local_max, float(row[i]));
    }
    shared[tid] = local_max;
    threadgroup_barrier(mem_flags::mem_threadgroup);
    
    for (uint stride = 128; stride > 0; stride >>= 1) {
        if (tid < stride) {
            shared[tid] = max(shared[tid], shared[tid + stride]);
        }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    float row_max = shared[0];
    
    // Pass 2: Compute exp(x - max) and sum
    float local_sum = 0.0f;
    for (uint i = tid; i < cols; i += 256) {
        float val = exp(float(row[i]) - row_max);
        row[i] = half(val);  // Temporarily store exp values
        local_sum += val;
    }
    shared[tid] = local_sum;
    threadgroup_barrier(mem_flags::mem_threadgroup);
    
    for (uint stride = 128; stride > 0; stride >>= 1) {
        if (tid < stride) {
            shared[tid] += shared[tid + stride];
        }
        threadgroup_barrier(mem_flags::mem_threadgroup);
    }
    float row_sum = shared[0];
    
    // Pass 3: Normalize
    float inv_sum = 1.0f / row_sum;
    for (uint i = tid; i < cols; i += 256) {
        row[i] = half(float(row[i]) * inv_sum);
    }
}
```

### C# Integration via P/Invoke

```csharp
// MetalBackend.cs

public sealed class MetalBackend : IComputeBackend
{
    private readonly IntPtr _device;
    private readonly IntPtr _commandQueue;
    private readonly Dictionary<string, IntPtr> _pipelines;
    
    // Metal framework bindings
    [DllImport(”/System/Library/Frameworks/Metal.framework/Metal”)]
    private static extern IntPtr MTLCreateSystemDefaultDevice();
    
    [DllImport(”minerva_apple.dylib”)]
    private static extern IntPtr minerva_create_pipeline(
        IntPtr device, 
        [MarshalAs(UnmanagedType.LPUTF8Str)] string functionName);
    
    [DllImport(”minerva_apple.dylib”)]
    private static extern void minerva_dispatch_attention(
        IntPtr queue,
        IntPtr pipeline,
        IntPtr q, IntPtr k, IntPtr v, IntPtr output,
        uint seqQ, uint seqKV, uint numHeads, uint headDim,
        float scale);
    
    [DllImport(”minerva_apple.dylib”)]
    private static extern void minerva_dispatch_matmul_q4_0(
        IntPtr queue,
        IntPtr pipeline,
        IntPtr weightsQuantized,
        IntPtr activations,
        IntPtr output,
        uint M, uint N, uint K);
    
    public MetalBackend()
    {
        _device = MTLCreateSystemDefaultDevice();
        if (_device == IntPtr.Zero)
            throw new PlatformNotSupportedException(”Metal not available”);
        
        _commandQueue = CreateCommandQueue(_device);
        _pipelines = LoadShaderLibrary();
    }
    
    public void FlashAttention(
        ReadOnlySpan<Half> q, ReadOnlySpan<Half> k, ReadOnlySpan<Half> v,
        Span<Half> output,
        int seqQ, int seqKV, int numHeads, int headDim)
    {
        float scale = 1.0f / MathF.Sqrt(headDim);
        
        // Pin managed memory and dispatch
        unsafe
        {
            fixed (Half* pQ = q, pK = k, pV = v, pO = output)
            {
                minerva_dispatch_attention(
                    _commandQueue,
                    _pipelines[”flash_attention_forward”],
                    (IntPtr)pQ, (IntPtr)pK, (IntPtr)pV, (IntPtr)pO,
                    (uint)seqQ, (uint)seqKV, (uint)numHeads, (uint)headDim,
                    scale);
            }
        }
    }
}
```

### Backend Dispatch Strategy

```csharp
// InferenceEngine.cs

public sealed class InferenceEngine
{
    private readonly IComputeBackend _metalBackend;
    private readonly IComputeBackend _accelerateBackend;
    
    // Threshold: use AMX for MatMul above this size
    private const int MATMUL_AMX_THRESHOLD = 1_000_000;  // M × N × K
    
    public void Forward(TransformerLayer layer, Tensor hidden)
    {
        // RMSNorm → Metal (memory-bound, parallel)
        _metalBackend.RMSNorm(hidden, layer.InputNorm);
        
        // QKV Projection → AMX (large GEMM)
        var qkvSize = hidden.Rows * layer.QKVWeight.Cols * layer.QKVWeight.Rows;
        if (qkvSize > MATMUL_AMX_THRESHOLD)
            _accelerateBackend.MatMul(hidden, layer.QKVWeight, layer.QKV);
        else
            _metalBackend.MatMul(hidden, layer.QKVWeight, layer.QKV);
        
        // RoPE → Metal (parallel rotation)
        _metalBackend.RoPE(layer.Q, layer.K, _position, layer.HeadDim);
        
        // Attention → Metal (Flash Attention)
        _metalBackend.FlashAttention(
            layer.Q, layer.K, layer.V, layer.AttnOutput,
            _seqLen, _kvCacheLen, layer.NumHeads, layer.HeadDim);
        
        // Output Projection → AMX
        _accelerateBackend.MatMul(layer.AttnOutput, layer.OutputWeight, hidden);
        
        // FFN: Gate + Up projection → AMX
        _accelerateBackend.MatMul(hidden, layer.GateWeight, layer.GateOutput);
        _accelerateBackend.MatMul(hidden, layer.UpWeight, layer.UpOutput);
        
        // SiLU + elementwise multiply → Metal
        _metalBackend.SiLU(layer.GateOutput);
        _metalBackend.Multiply(layer.GateOutput, layer.UpOutput, layer.FFNHidden);
        
        // Down projection → AMX
        _accelerateBackend.MatMul(layer.FFNHidden, layer.DownWeight, hidden);
    }
}
```

### KV-Cache Management

```csharp
// KVCache.cs

/// <summary>
/// Pre-allocated KV cache for autoregressive generation.
/// Stored in unified memory, accessible by both CPU and GPU.
/// </summary>
public sealed class KVCache : IDisposable
{
    private readonly Half[] _keyCache;    // [layers, max_seq, num_heads, head_dim]
    private readonly Half[] _valueCache;  // [layers, max_seq, num_heads, head_dim]
    private readonly int _numLayers;
    private readonly int _maxSeqLen;
    private readonly int _numHeads;
    private readonly int _headDim;
    
    private int _currentLen;
    
    public KVCache(ModelConfig config, int maxSeqLen)
    {
        _numLayers = config.NumLayers;
        _maxSeqLen = maxSeqLen;
        _numHeads = config.NumKVHeads;  // May differ from Q heads (GQA)
        _headDim = config.HeadDim;
        
        long cacheSize = (long)_numLayers * _maxSeqLen * _numHeads * _headDim;
        
        _keyCache = new Half[cacheSize];
        _valueCache = new Half[cacheSize];
        _currentLen = 0;
        
        // Memory estimate logging
        long bytesPerCache = cacheSize * sizeof(ushort);  // Half = 2 bytes
        Console.WriteLine($”KV-Cache allocated: {2 * bytesPerCache / (1024 * 1024)} MB”);
    }
    
    /// <summary>
    /// Append new K, V values for current generation step.
    /// </summary>
    public void Append(int layer, ReadOnlySpan<Half> newK, ReadOnlySpan<Half> newV)
    {
        int offset = GetOffset(layer, _currentLen);
        newK.CopyTo(_keyCache.AsSpan(offset));
        newV.CopyTo(_valueCache.AsSpan(offset));
    }
    
    public void IncrementPosition() => _currentLen++;
    
    public ReadOnlySpan<Half> GetKeys(int layer) => 
        _keyCache.AsSpan(GetOffset(layer, 0), _currentLen * _numHeads * _headDim);
    
    public ReadOnlySpan<Half> GetValues(int layer) =>
        _valueCache.AsSpan(GetOffset(layer, 0), _currentLen * _numHeads * _headDim);
    
    private int GetOffset(int layer, int seqPos) =>
        ((layer * _maxSeqLen) + seqPos) * _numHeads * _headDim;
}
```

## Consequences

### Benefits

- **Full M4 Max utilization**: GPU for parallel ops, AMX for dense GEMM
- **Memory efficiency**: Flash Attention avoids O(n²) allocation
- **Quantization support**: In-kernel dequantization, no memory expansion
- **Zero-copy KV-cache**: Unified memory enables direct GPU access
- **Semantic sovereignty**: Direct Metal API, no wrapper frameworks

### Costs

- **Platform-specific code**: Metal shaders only run on Apple Silicon
- **Shader complexity**: Flash Attention is non-trivial to debug
- **Testing requirements**: Need correctness validation against reference

### Risks and Mitigations

| Risk                      | Mitigation                                  |
|---------------------------|---------------------------------------------|
| Metal API changes         | Pin to stable macOS SDK version             |
| Numerical precision       | Compare outputs against llama.cpp reference |
| Threadgroup memory limits | Tile sizes tuned for 32 KB shared memory    |
| KV-cache overflow         | Explicit max_seq_len, sliding window option |

## Implementation Order

### Phase 1: Foundation

- [ ] Metal device initialization and command queue setup
- [ ] Shader compilation pipeline (xcrun metallib)
- [ ] P/Invoke bindings for dispatch
- [ ] Basic buffer management

### Phase 2: Core Shaders

- [ ] `elementwise.metal`: SiLU, RMSNorm
- [ ] `softmax.metal`: Standalone softmax
- [ ] `rope.metal`: Rotary embeddings
- [ ] Unit tests against CPU reference

### Phase 3: Attention

- [ ] `attention.metal`: Flash Attention forward
- [ ] KV-cache integration
- [ ] Causal masking validation
- [ ] Benchmark vs naive attention

### Phase 4: Quantization

- [ ] `quantized_matmul.metal`: Q4_0 kernel
- [ ] Q8_0 variant
- [ ] K-quant support (Q4_K, Q5_K)
- [ ] Accuracy validation

### Phase 5: Integration

- [ ] Backend dispatch logic (Metal vs AMX)
- [ ] End-to-end inference pipeline
- [ ] Benchmark suite (tokens/sec)

## Success Criteria

- [ ] Flash Attention matches naive attention output (ε < 1e-3)
- [ ] Q4_0 inference within 1% perplexity of FP16 reference
- [ ] Token generation ≥ 30 tokens/sec for Llama 8B Q4
- [ ] KV-cache supports 32K context without OOM
- [ ] All shaders compile on macOS 14+ SDK

## References

### Academic Papers

- Flash Attention: https://arxiv.org/abs/2205.14135 (Dao et al., 2022)
- Flash Attention 2: https://arxiv.org/abs/2307.08691 (Dao, 2023)
- RoPE: https://arxiv.org/abs/2104.09864 (Su et al., 2021)
- GQA: https://arxiv.org/abs/2305.13245 (Ainslie et al., 2023)

### Vendor Documentation

- Metal Shading Language: https://developer.apple.com/metal/Metal-Shading-Language-Specification.pdf
- Metal Best Practices: https://developer.apple.com/documentation/metal/metal_best_practices_guide
- Metal Performance Shaders: https://developer.apple.com/documentation/metalperformanceshaders

### Reference Implementations

- llama.cpp Metal: https://github.com/ggerganov/llama.cpp/blob/master/ggml/src/ggml-metal.m
- MLX (Apple): https://github.com/ml-explore/mlx