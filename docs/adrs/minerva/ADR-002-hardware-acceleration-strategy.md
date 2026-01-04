# ADR-002: Hardware Acceleration Strategy

## Status

Proposed

## Related ADRs

- [ADR-001: Weight and Tokenizer Format Support](ADR-001-weight-formats.md) - Foundation for model loading

## Context

Minerva requires hardware acceleration for viable inference performance. The question isn’t whether to accelerate, but how to do so while maintaining semantic sovereignty — owning our abstractions, avoiding package pollution, and going direct to hardware APIs.

### The Principle

BCL-only is about **semantic sovereignty**, not hardware avoidance:

- BCL when it provides direct hardware access (mmap, SIMD intrinsics)
- Vendor APIs when hardware requires them (Metal, CUDA, Accelerate)
- No wrapper packages, no abstraction layers, no third-party semantics

The dependency graph should be: `Minerva → Hardware API → Hardware`

Not: `Minerva → Package → Package’s dependencies → Hardware API → Hardware`

### Hardware Landscape

|Platform     |Accelerator |Access Path                    |.NET Status       |
|-————|————|-——————————|——————|
|x64          |AVX2/AVX-512|`System.Runtime.Intrinsics.X86`|✓ Full BCL        |
|ARM64        |NEON        |`System.Runtime.Intrinsics.Arm`|✓ Full BCL        |
|ARM64        |SVE/SVE2    |`System.Runtime.Intrinsics.Arm`|Partial, expanding|
|Apple Silicon|AMX         |`Accelerate.framework`         |P/Invoke required |
|Apple Silicon|ANE         |`CoreML` / `Metal`             |P/Invoke required |
|Apple Silicon|GPU         |`Metal.framework`              |P/Invoke required |
|NVIDIA       |CUDA Cores  |`CUDA Runtime API`             |P/Invoke required |
|NVIDIA       |Tensor Cores|`cuBLAS` / `cuDNN`             |P/Invoke required |

## Decision

### Multi-Backend Architecture

Minerva implements a pluggable backend system with compile-time and runtime selection:

```
Minerva.dll (C#, BCL)
    │
    ├── IComputeBackend (abstraction)
    │
    ├── CpuBackend (BCL intrinsics)
    │   └── System.Runtime.Intrinsics
    │
    ├── AppleBackend (P/Invoke)
    │   ├── Accelerate.framework (AMX, vecLib)
    │   └── Metal.framework (GPU compute)
    │
    └── CudaBackend (P/Invoke)
        ├── CUDA Runtime API
        └── cuBLAS
```

### Backend Interface

```csharp
/// <summary>
/// Compute backend abstraction for tensor operations.
/// Implementations go direct to hardware APIs - no wrapper packages.
/// </summary>
public interface IComputeBackend : IDisposable
{
    string Name { get; }
    ComputeCapabilities Capabilities { get; }
    
    // Core operations - signatures mirror hardware primitives
    void MatMul(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> c,
                int m, int n, int k);
    void MatMulF16(ReadOnlySpan<Half> a, ReadOnlySpan<Half> b, Span<Half> c,
                   int m, int n, int k);
    void MatMulQuantized(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<float> c,
                         int m, int n, int k, QuantizationType quant);
    
    // Attention
    void SoftmaxInPlace(Span<float> x, int rows, int cols);
    void ScaledDotProductAttention(/* ... */);
    
    // Elementwise
    void SiLU(Span<float> x);
    void RMSNorm(Span<float> x, ReadOnlySpan<float> weight, float eps);
    void RoPE(Span<float> q, Span<float> k, int headDim, int position);
}

[Flags]
public enum ComputeCapabilities
{
    None = 0,
    Float32 = 1 << 0,
    Float16 = 1 << 1,
    BFloat16 = 1 << 2,
    Int8Quantized = 1 << 3,
    Int4Quantized = 1 << 4,
    BatchedMatMul = 1 << 5,
    FusedAttention = 1 << 6
}
```

### Backend Selection

```csharp
public static class ComputeBackend
{
    public static IComputeBackend Create(ComputePreference preference = default)
    {
        return preference.Preferred switch
        {
            BackendType.Cpu => new CpuBackend(),
            BackendType.Apple => TryCreateApple() ?? new CpuBackend(),
            BackendType.Cuda => TryCreateCuda() ?? new CpuBackend(),
            BackendType.Auto => AutoSelect(),
            _ => new CpuBackend()
        };
    }
    
    private static IComputeBackend AutoSelect()
    {
        // Prefer GPU backends when available
        if (TryCreateCuda() is { } cuda) return cuda;
        if (TryCreateApple() is { } apple) return apple;
        return new CpuBackend();
    }
}
```

## Implementation

### Phase 1: CPU Backend (BCL Only)

Pure .NET implementation using `System.Runtime.Intrinsics`:

```csharp
public sealed class CpuBackend : IComputeBackend
{
    public string Name => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 when Avx2.IsSupported => ”CPU/AVX2”,
        Architecture.X64 when Avx512F.IsSupported => ”CPU/AVX-512”,
        Architecture.Arm64 when AdvSimd.IsSupported => ”CPU/NEON”,
        _ => ”CPU/Scalar”
    };
    
    public void MatMul(ReadOnlySpan<float> a, ReadOnlySpan<float> b, 
                       Span<float> c, int m, int n, int k)
    {
        if (Avx2.IsSupported)
            MatMulAvx2(a, b, c, m, n, k);
        else if (AdvSimd.IsSupported)
            MatMulNeon(a, b, c, m, n, k);
        else
            MatMulScalar(a, b, c, m, n, k);
    }
    
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MatMulAvx2(ReadOnlySpan<float> a, ReadOnlySpan<float> b,
                                    Span<float> c, int m, int n, int k)
    {
        // Direct AVX2 intrinsics - 8-wide SIMD
        // Reference: Intel Intrinsics Guide
        // https://www.intel.com/content/www/us/en/docs/intrinsics-guide/
    }
    
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static void MatMulNeon(ReadOnlySpan<float> a, ReadOnlySpan<float> b,
                                    Span<float> c, int m, int n, int k)
    {
        // Direct NEON intrinsics - 4-wide SIMD (float32x4)
        // Reference: Arm NEON Intrinsics Reference
        // https://developer.arm.com/architectures/instruction-sets/intrinsics/
    }
}
```

**Vendor References:**

- Intel Intrinsics Guide: https://www.intel.com/content/www/us/en/docs/intrinsics-guide/
- Arm NEON Intrinsics: https://developer.arm.com/architectures/instruction-sets/intrinsics/
- .NET Hardware Intrinsics: https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics

### Phase 2: Apple Backend (P/Invoke)

Direct calls to Apple frameworks via P/Invoke:

```csharp
public sealed class AppleBackend : IComputeBackend
{
    public string Name => ”Apple/Accelerate+Metal”;
    
    // Accelerate.framework - BLAS operations (uses AMX on Apple Silicon)
    [DllImport(”/System/Library/Frameworks/Accelerate.framework/Accelerate”)]
    private static extern void cblas_sgemm(
        CBLAS_ORDER order, CBLAS_TRANSPOSE transA, CBLAS_TRANSPOSE transB,
        int m, int n, int k,
        float alpha, float* a, int lda,
        float* b, int ldb,
        float beta, float* c, int ldc);
    
    // vDSP for vectorized operations
    [DllImport(”/System/Library/Frameworks/Accelerate.framework/Accelerate”)]
    private static extern void vDSP_vsadd(
        float* a, int strideA,
        float* b,
        float* c, int strideC,
        uint count);
    
    public unsafe void MatMul(ReadOnlySpan<float> a, ReadOnlySpan<float> b,
                              Span<float> c, int m, int n, int k)
    {
        fixed (float* pa = a, pb = b, pc = c)
        {
            cblas_sgemm(CBLAS_ORDER.RowMajor, 
                       CBLAS_TRANSPOSE.NoTrans, CBLAS_TRANSPOSE.NoTrans,
                       m, n, k,
                       1.0f, pa, k,
                       pb, n,
                       0.0f, pc, n);
        }
    }
}
```

**Metal Compute (GPU):**

```csharp
public sealed class MetalComputeBackend : IComputeBackend
{
    private readonly IntPtr _device;
    private readonly IntPtr _commandQueue;
    private readonly IntPtr _matmulPipeline;
    
    // Metal framework bindings
    [DllImport(”/System/Library/Frameworks/Metal.framework/Metal”)]
    private static extern IntPtr MTLCreateSystemDefaultDevice();
    
    [DllImport(”/System/Library/Frameworks/Metal.framework/Metal”)]
    private static extern IntPtr MTLDevice_newCommandQueue(IntPtr device);
    
    // Shader compilation from MSL (Metal Shading Language)
    private static readonly string MatMulShader = ”””
        #include <metal_stdlib>
        using namespace metal;
        
        kernel void matmul(
            device const float* a [[buffer(0)]],
            device const float* b [[buffer(1)]],
            device float* c [[buffer(2)]],
            constant uint& M [[buffer(3)]],
            constant uint& N [[buffer(4)]],
            constant uint& K [[buffer(5)]],
            uint2 gid [[thread_position_in_grid]])
        {
            if (gid.x >= N || gid.y >= M) return;
            
            float sum = 0.0f;
            for (uint i = 0; i < K; i++) {
                sum += a[gid.y * K + i] * b[i * N + gid.x];
            }
            c[gid.y * N + gid.x] = sum;
        }
        ”””;
}
```

**Vendor References:**

- Accelerate Framework: https://developer.apple.com/documentation/accelerate
- BLAS Reference: https://developer.apple.com/documentation/accelerate/blas
- vDSP Reference: https://developer.apple.com/documentation/accelerate/vdsp
- Metal Compute: https://developer.apple.com/documentation/metal
- Metal Shading Language: https://developer.apple.com/metal/Metal-Shading-Language-Specification.pdf

### Phase 3: CUDA Backend (P/Invoke)

Direct CUDA Runtime API and cuBLAS:

```csharp
public sealed class CudaBackend : IComputeBackend
{
    public string Name => $”CUDA/{_deviceName}”;
    
    // CUDA Runtime API
    [DllImport(”cudart64_12”, EntryPoint = ”cudaMalloc”)]
    private static extern CudaError cudaMalloc(out IntPtr devPtr, ulong size);
    
    [DllImport(”cudart64_12”, EntryPoint = ”cudaMemcpy”)]
    private static extern CudaError cudaMemcpy(IntPtr dst, IntPtr src, 
                                                ulong count, CudaMemcpyKind kind);
    
    [DllImport(”cudart64_12”, EntryPoint = ”cudaFree”)]
    private static extern CudaError cudaFree(IntPtr devPtr);
    
    // cuBLAS for optimized GEMM
    [DllImport(”cublas64_12”, EntryPoint = ”cublasCreate_v2”)]
    private static extern CublasStatus cublasCreate(out IntPtr handle);
    
    [DllImport(”cublas64_12”, EntryPoint = ”cublasSgemm_v2”)]
    private static extern CublasStatus cublasSgemm(
        IntPtr handle, CublasOperation transa, CublasOperation transb,
        int m, int n, int k,
        ref float alpha, IntPtr a, int lda,
        IntPtr b, int ldb,
        ref float beta, IntPtr c, int ldc);
    
    public void MatMul(ReadOnlySpan<float> a, ReadOnlySpan<float> b,
                       Span<float> c, int m, int n, int k)
    {
        // Allocate device memory
        cudaMalloc(out var devA, (ulong)(m * k * sizeof(float)));
        cudaMalloc(out var devB, (ulong)(k * n * sizeof(float)));
        cudaMalloc(out var devC, (ulong)(m * n * sizeof(float)));
        
        try
        {
            // Copy to device
            fixed (float* pa = a, pb = b)
            {
                cudaMemcpy(devA, (IntPtr)pa, (ulong)(m * k * sizeof(float)), 
                          CudaMemcpyKind.HostToDevice);
                cudaMemcpy(devB, (IntPtr)pb, (ulong)(k * n * sizeof(float)), 
                          CudaMemcpyKind.HostToDevice);
            }
            
            // Execute GEMM
            float alpha = 1.0f, beta = 0.0f;
            cublasSgemm(_handle, CublasOperation.None, CublasOperation.None,
                       n, m, k, ref alpha, devB, n, devA, k, ref beta, devC, n);
            
            // Copy back
            fixed (float* pc = c)
            {
                cudaMemcpy((IntPtr)pc, devC, (ulong)(m * n * sizeof(float)),
                          CudaMemcpyKind.DeviceToHost);
            }
        }
        finally
        {
            cudaFree(devA);
            cudaFree(devB);
            cudaFree(devC);
        }
    }
}
```

**Vendor References:**

- CUDA Runtime API: https://docs.nvidia.com/cuda/cuda-runtime-api/
- cuBLAS: https://docs.nvidia.com/cuda/cublas/
- CUDA C++ Programming Guide: https://docs.nvidia.com/cuda/cuda-c-programming-guide/

## Native Library Structure

```
minerva-native/
├── cpu/
│   └── (empty - pure BCL)
├── apple/
│   ├── minerva_apple.m          # Objective-C bridge
│   ├── minerva_metal.metal      # Metal compute shaders
│   └── build.sh                 # clang build script
├── cuda/
│   ├── minerva_cuda.cu          # CUDA kernels
│   └── build.sh                 # nvcc build script
└── build-all.sh
```

**Build artifacts:**

- `minerva_apple.dylib` — macOS native (Accelerate + Metal)
- `minerva_cuda.dll/.so` — Windows/Linux CUDA

## Quantization Support

Quantized inference is critical for practical model sizes. Each backend implements:

|Format|Description     |CPU|Apple|CUDA|
|——|-—————|—|——|-—|
|Q8_0  |8-bit symmetric |✓  |✓    |✓   |
|Q4_0  |4-bit symmetric |✓  |✓    |✓   |
|Q4_1  |4-bit asymmetric|✓  |✓    |✓   |
|Q4_K  |K-quant variant |✓  |✓    |✓   |

Dequantization happens in the kernel — no intermediate float expansion.

**Reference:**

- GGML Quantization: https://github.com/ggerganov/ggml/blob/master/docs/quantization.md

## Consequences

### Benefits

- **Full hardware utilization**: AMX, ANE, CUDA Tensor Cores accessible
- **No package pollution**: Direct vendor APIs, owned semantics
- **Graceful degradation**: CPU fallback always available
- **Build simplicity**: Native libs are small, focused shims
- **AI-assisted development**: Metal/CUDA kernels viable with LLM collaboration

### Costs

- **Multi-platform native builds**: CI/CD complexity for native libs
- **API surface maintenance**: Track vendor API changes
- **Testing matrix**: Each backend needs validation

### Risks and Mitigations

|Risk                  |Mitigation                                             |
|-———————|-——————————————————|
|Vendor API instability|Pin to stable versions; abstract behind IComputeBackend|
|Platform-specific bugs|Comprehensive test suite per backend; CPU as reference |
|Build complexity      |Containerized builds; pre-built binaries for releases  |
|ANE access limitations|Accept Metal GPU path; ANE via CoreML is black box     |

## Implementation Order

### Phase 1: CPU Backend

- [ ] `CpuBackend` with scalar fallback
- [ ] AVX2 MatMul kernel
- [ ] NEON MatMul kernel
- [ ] SoftMax, SiLU, RMSNorm implementations
- [ ] Benchmark suite (reference baseline)

### Phase 2: Apple Backend

- [ ] Accelerate.framework P/Invoke bindings
- [ ] `cblas_sgemm` integration
- [ ] vDSP vectorized operations
- [ ] Metal compute pipeline setup
- [ ] Metal MatMul shader
- [ ] Benchmark vs CPU backend

### Phase 3: CUDA Backend

- [ ] CUDA Runtime P/Invoke bindings
- [ ] cuBLAS integration
- [ ] Memory pool for device allocations
- [ ] Async copy/compute overlap
- [ ] Benchmark vs CPU backend

### Phase 4: Quantization

- [ ] Q8_0 dequantize + matmul (all backends)
- [ ] Q4_0/Q4_1 dequantize + matmul (all backends)
- [ ] K-quant support (GGUF compatibility)

## Success Criteria

- [ ] CPU backend achieves ≥80% of llama.cpp CPU throughput
- [ ] Apple backend utilizes AMX (visible in Instruments)
- [ ] CUDA backend utilizes Tensor Cores where available
- [ ] All backends pass identical correctness tests
- [ ] Backend selection is runtime-configurable
- [ ] Native libraries build in CI for all platforms

## References

### Vendor Documentation

- Intel Intrinsics Guide: https://www.intel.com/content/www/us/en/docs/intrinsics-guide/
- Arm Intrinsics Reference: https://developer.arm.com/architectures/instruction-sets/intrinsics/
- Apple Accelerate: https://developer.apple.com/documentation/accelerate
- Apple Metal: https://developer.apple.com/documentation/metal
- NVIDIA CUDA: https://docs.nvidia.com/cuda/
- NVIDIA cuBLAS: https://docs.nvidia.com/cuda/cublas/

### .NET Documentation

- Hardware Intrinsics: https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics
- P/Invoke: https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke

### Reference Implementations

- llama.cpp: https://github.com/ggerganov/llama.cpp (architecture reference, not dependency)
- ggml: https://github.com/ggerganov/ggml (quantization formats)