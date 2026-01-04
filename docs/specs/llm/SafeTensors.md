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
