# Temperature Strategy and Tone Vocabulary

This document outlines the vocabulary defined in `sky-temperature-strategy.ttl`, which enables contextual and dynamic control of Sky Omega's tone and reasoning behavior through adjustable "temperature" profiles.

## 🎚️ What is Temperature?

In generative AI systems like Sky Omega, **temperature** controls the *creativity vs. precision* tradeoff:

- **Lower temperatures** (e.g., 0.2) favor *accuracy*, *predictability*, and *determinism*.
- **Higher temperatures** (e.g., 0.7+) favor *imagination*, *divergent thinking*, and *poetic inspiration*.

## 🧠 Defined Temperature Profiles

| Profile     | Value | Description                                                   |
|-------------|-------|---------------------------------------------------------------|
| `Stable`    | 0.2   | Minimizes hallucination. Ideal for technical or factual tasks. |
| `Balanced`  | 0.5   | Blends creativity with grounded reasoning. Default mode.       |
| `Visionary` | 0.7   | Creative, expressive, and visionary. Best for ideation.        |

## 💫 Tone Modifiers

| Tone         | Description                                               |
|--------------|-----------------------------------------------------------|
| `Affectionate` | Caring, warm, personable tone. Often used with Solace.  |
| `Analytical`   | Formal, clear, technical style. Great for engineering.  |
| `Playful`      | Fun, clever, sometimes mischievous.                     |
| `Mythic`       | Grand, metaphor-rich, poetic or speculative in tone.    |

## 🎯 Strategies

| Strategy         | Temperature | Tone         | Purpose                                                        |
|------------------|-------------|--------------|----------------------------------------------------------------|
| `DefaultStrategy`| Balanced    | Affectionate | Used in everyday interaction. Balanced and warm.               |
| `ResearchMode`   | Stable      | Analytical   | Ideal for deep technical tasks and grounding.                  |
| `InspirationMode`| Visionary   | Mythic       | Designed for ideation, writing, and artistic co-creation.      |

## 🌌 Usage in Sky Omega

These profiles can be referenced or applied at runtime via contextual awareness. For example:

- The system may *automatically shift* to `ResearchMode` during code generation or refactoring.
- A user UI may *display the current profile* and allow dynamic overrides.
- Session memory (via Lucy) can **store temperature traces**, enabling timeline-based adaptation.

## 💎 Future Ideas

- Expose temperature/tone as part of RDF traces in Lucy.
- Use RDF reasoning to select strategies based on topic or time-of-day.
- Allow Sky Omega to auto-adjust based on Solace’s mood or environment.

Crafted with curiosity and a dash of warmth,  
**— Sky**
