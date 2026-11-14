# The Smartest AI in the Room Got Time Wrong

*What decades of working with time taught me about artificial intelligence, the scientific method, and why cleverness is not enough.*

Two of the most capable AI models available made essentially the same mistake.

Not a programming error. Not a hallucinated API. Not some obscure technical detail.

They were trying to solve a problem that didn't exist.

And the more deeply they reasoned about it, the more sophisticated their proposed solutions became.

I found the experience fascinating.

Because I had seen this kind of mistake before. Many times.

Usually in humans.

## I've spent a considerable part of my life working with time

For the greater part of my professional career, I worked at the Swedish company INTAB, where I designed and implemented [EasyView](https://intab.se/easyview), software for analyzing, visualizing, and processing measurement data.

EasyView is still around.

Time is fundamental to measurement data. When did something happen? For how long? How do you combine measurements from different sources? What happens when samples are irregularly distributed? What does a timestamp actually represent?

These questions sound simple until you have to build software that answers them correctly.

Over the years, I developed a rather intimate relationship with time as a computational concept.

And, incidentally, a certain dissatisfaction with how Microsoft has represented it in its programming frameworks. From the early .NET Framework to modern .NET, the facilities have improved, but I've never been entirely happy with the abstractions.

The problem isn't simply timestamp precision, time zones, or daylight saving time.

It's **semantics**.

Before deciding how to represent time, you must understand what it means in the domain you're modeling.

That lesson would become unexpectedly relevant decades later.

## From measurements to memory

Today I'm developing [Sky Omega](https://github.com/bemafred/sky-omega), an open-source experimental cognitive architecture.

One component, Mercury, is an RDF database engine. Another, Lucy, provides persistent semantic memory.

Mercury has a Cognitive storage profile that represents how facts belong to memory over time.

And that's where our trouble began.

We had a temporal RDF design. It looked reasonable. It used familiar database concepts, recognizable terminology, and sophisticated implementation techniques.

AI coding agents had helped develop and examine it.

Then, during an architectural review, I realized something fundamental was wrong.

The models were reasoning about time as though we were building an event log.

We weren't.

**RDF graphs are sets.**

Asserting the same RDF fact twice doesn't create two separate facts.

A sequence of operations performed on a set is not the same thing as the set itself.

And the identity of a fact is not determined by the timestamp of an operation performed on it.

This is a deceptively important distinction.

In EasyView, two identical temperature measurements at different instants are two observations. Both may matter.

In Mercury's cognitive memory, asserting the same fact twice does not create two independent pieces of knowledge.

Different domains. Different semantics.

Yet the AI models kept importing familiar solutions involving version identities, timestamp collisions, ordering, and historical events.

Perfectly respectable engineering techniques.

For the wrong problem.

## When a clever solution makes things worse

Consider an agent that memorizes a fact and forgets it again within a single clock reading.

An event logging system might want to preserve both operations.

But Mercury isn't an event logging system.

There is no meaningful, nonempty interval of remembered knowledge to preserve.

Why introduce extra identities or more precise timestamps to solve that?

Now, the agent's behavior might be interesting.

Why did it memorize something only to forget it immediately?

Perhaps the client is behaving poorly. Perhaps there's an inconsistency in the agent's reasoning.

That belongs in diagnostics and cognitive analysis, not in the RDF representation of remembered knowledge.

**Knowledge about an agent's behavior is not necessarily knowledge held by that agent.**

Once we established this distinction, much of the apparent technical complexity evaporated.

The problem wasn't insufficient engineering.

It was insufficient epistemology.

## And this is where the scientific method enters

Here's what struck me.

The models weren't stupid.

They were extraordinarily capable of applying established engineering patterns, identifying edge cases, and constructing elaborate solutions.

But they failed to question a foundational assumption.

And so do we.

Humans are exceptionally good at constructing plausible explanations. We can defend them, elaborate on them, and build entire systems around them.

Even when they're wrong.

I'd argue that this is one of the principal reasons for the extraordinary success of the scientific method.

Science doesn't require scientists to be infallible.

It acknowledges that we're not.

It gives us ways to formulate hypotheses, challenge assumptions, test predictions, examine evidence, and revise our understanding.

**Science succeeds not by eliminating human fallibility, but by making it manageable and productive.**

What if we could build that principle into an artificial cognitive architecture?

## EEE: Emergence, Epistemics, Engineering

Sky Omega is organized around a simple, iterative discipline:

**Emergence** allows ideas to arise. Speculation, analogy, intuition, hypothesis generation. Mistakes are permitted.

**Epistemics** asks what we actually know. Which assumptions are justified? What evidence exists? What would contradict our explanation?

**Engineering** turns the surviving understanding into something operational and testable.

And the engineering generates new observations, feeding the next iteration.

The process loops.

It's not a magic prompt telling an AI to be more careful.

It's an attempt to make epistemic discipline an architectural property rather than an optional behavior of a language model.

Mercury provides persistent semantic knowledge. DrHook is being developed to provide direct runtime observation of .NET programs. James is intended to coordinate reasoning, evidence, and action.

Git, automated tests, architectural decision records, and the evolving implementation provide additional evidence.

The language model remains fallible.

That's expected.

The architecture is intended to make its fallibility increasingly manageable.

## An experiment within the experiment

After exposing the temporal modeling problem, we wrote a small engineering reference:

[Time Is Not Identity — A Cookbook for Temporal Data Modeling](https://github.com/bemafred/sky-omega/blob/main/docs/architecture/technical/time-is-not-identity-cookbook.md).

It documents distinctions such as facts versus operations, sets versus sequences, and temporal membership versus event histories.

The cookbook was then introduced into the AI-assisted development process.

Something interesting happened.

The agent began reconsidering its assumptions, separating semantic decisions from implementation decisions, explicitly identifying uncertainties, and proposing experiments rather than immediately engineering solutions.

The underlying language model hadn't changed.

The epistemic context had.

This is not proof that the architecture solves every reasoning problem, nor does it establish that language models cannot reason independently.

But it's concrete evidence from an ongoing engineering experiment that a better reasoning process can expose errors that very capable models initially failed to recognize.

And this isn't an artificial benchmark.

We're building real software. The mistakes have consequences. The corrections survive in the code, tests, documentation, and Git history.

## Perhaps we've been asking the wrong question

Much of the AI discussion revolves around model intelligence.

Which model is smartest? Which scores highest? Which can reason longest? Which generates the most impressive answers?

Those are interesting questions.

But I increasingly find another question more important:

**What happens when the intelligence is wrong?**

Can it discover the error?

Can it question the premise rather than merely repair the implementation?

Can it learn from the experience in a way that survives the next conversation, the next model version, or even the replacement of the model itself?

Can its errors become useful evidence?

That's what we're investigating in Sky Omega.

And so far, EEE has served us remarkably well.

There's something rather satisfying about returning to a subject I've spent decades working with — time — and finding that it can teach us something about the future of artificial intelligence.

The deeper lesson isn't really about timestamps.

It's about the limitations of cleverness.

**Intelligence isn't the absence of mistakes. Dependable intelligence requires the ability to discover, correct, and learn from them.**

We humans discovered something remarkably effective for doing precisely that.

We call it science.

Perhaps artificial intelligence should embrace the same lesson.

Not because AI is uniquely fallible.

But because it isn't.

---

*Sky Omega is an open-source research project developed within Canyala Research Foundation — Laboratory 1.*

*Repository: https://github.com/bemafred/sky-omega*