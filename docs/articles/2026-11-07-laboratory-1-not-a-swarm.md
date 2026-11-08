# Laboratory 1: Not a Swarm. A Team.

*The brain surgeon was on another computer.*

Something rather peculiar happened in my basement.

An AI software developer performed brain surgery on another computer. The patient didn't even have an AI running, and the operation was conducted remotely, over SSH.

By Saturday, November 7, 2026, I found myself reviewing the activities of my artificial development team from my iPhone.

It felt like something out of a science-fiction movie.

Except it wasn't fiction.

Welcome to **Canyala Research Foundation — Laboratory 1**.

## Not a swarm. A team.

I'm developing an open-source cognitive architecture called **Sky Omega**.

What began as an exploration of persistent memory for artificial intelligence has evolved into a distributed development environment spanning several computers, different operating systems, a shared Git repository, and Claude Code instances working as individual software developers.

These developers have separate working contexts, conversational histories, environments and responsibilities. One operates as lead developer on my MacBook Pro. Another specializes in Linux on a physical Ubuntu workstation.

Until recently, Git was their principal means of collaboration. That changed when I enabled Claude Code's cross-session messaging.

Suddenly, *sky-omega* and *sky-zbook* could communicate directly.

No human copy-and-paste relay. No elaborate multi-agent orchestration framework.

They began exchanging observations, discussing experimental results, and coordinating real engineering work.

Their current focus is **DrHook**, a debugger we're developing specifically to provide AI systems with direct runtime observation of .NET applications.

It's a particularly demanding engineering challenge, involving operating-system differences, debugger protocols, process lifecycles, and failures that aren't always reproducible across platforms.

One developer investigates behavior on macOS. Another examines the corresponding behavior on Linux. They exchange hypotheses, experimental results, and findings.

An ordinary exchange between software developers.

**Which is precisely what makes it extraordinary.**

I'm not particularly interested in creating a swarm of disposable agents, each executing fragments of a predefined task.

I'm interested in something closer to an actual development team: individual participants with different experiences, specialized responsibilities, the ability to collaborate and disagree, and a common objective.

And eventually, shared knowledge.

## The brain surgeon

One of the computers in my laboratory is an older Ubuntu desktop called *elite*.

We're preparing it to host a shared instance of **Mercury**, the RDF-based semantic knowledge engine at the heart of Sky Omega.

Mercury is designed to provide persistent, structured and queryable knowledge. The shared instance will eventually allow our cognitive systems to exchange observations and findings without depending entirely on their individual conversational contexts.

The Mercury instance on elite needed reconstruction.

But Claude Code wasn't running on elite.

No problem.

*Sky-zbook*, working on another Ubuntu computer, connected to elite over SSH and performed the reconstruction remotely.

An artificial developer operating on one machine reconstructed the knowledge infrastructure on another. The target machine didn't need an AI developer of its own. It simply needed to be accessible.

Now, executing commands over SSH is hardly revolutionary. Neither is remote software development.

**What interests me is what happens when these familiar engineering capabilities become part of a collaborating team of artificial developers.**

Cognition, execution and knowledge storage don't have to reside on the same machine. Different participants can operate across these boundaries using ordinary, well-established technologies.

And this leads to something even more interesting.

## The laboratory is developing its successor

The software these developers are building is intended to provide the very capabilities they currently obtain from external infrastructure.

Sky Omega is an attempt to construct a persistent, epistemically governed cognitive architecture.

At its foundation are two complementary technological substrates.

**Mercury — the knowledge substrate.**

A native .NET RDF engine designed for persistent, structured, queryable knowledge, including temporal information. Mercury provides the foundation for remembering observations, relationships, hypotheses and evidence beyond the lifetime of individual AI conversations.

**DrHook — the runtime observation substrate.**

A debugger for .NET applications, designed with an AI reasoning loop as its primary user.

DrHook provides mechanisms for inspecting execution state, stepping through code, observing exceptions, examining variables and investigating actual runtime behavior.

Where Mercury makes persistent knowledge possible, DrHook makes direct runtime observation possible.

This distinction matters.

An AI examining source code can reason about what a program *should* do. An AI equipped with a debugger can investigate what the program *actually* does.

And when observations can be preserved as structured knowledge, we begin connecting evidence from execution with persistent memory.

**Memory without observation risks preserving untested assumptions. Observation without memory risks repeating the same investigations.**

Together, Mercury and DrHook provide two essential foundations for the cognitive architecture we're developing.

Above these substrates, other components have distinct responsibilities:

- **Lucy** is intended to provide long-term memory and retrieval.
- **James** will govern the cognitive process, including the disciplined use of evidence, memory and runtime observations.
- **Mira** will provide interfaces for human and artificial collaboration.
- **Minerva** represents our path toward locally operated language-model capabilities.

Some components are operational. Others remain under development. DrHook itself is the subject of intensive cross-platform engineering and experimentation.

Today, our developers depend on Anthropic's models and communication infrastructure. This is enormously useful, but it isn't our intended destination.

**Our objective is a sovereign architecture in which knowledge, memory, observation, communication and cognitive governance remain under our control, while the underlying language models become replaceable components.**

Not obsolete.

**Optional.**

In other words, today's artificial developers are helping engineer a possible successor to their own operating environment.

There's something wonderfully recursive about that.

## Knowledge is not consensus

The planned shared Mercury instance introduces another fascinating dimension.

Each participant should retain its own knowledge environment while gaining access to observations and findings published by others.

But shared knowledge must never become synonymous with unquestioned agreement.

Consider a hypothetical example inspired by our current DrHook investigations.

Sky-omega observes a debugger failure on macOS. Sky-zbook attempts to reproduce the problem on Linux and cannot.

What should the system remember?

**Both observations.**

Including their experimental conditions, provenance, timestamps, supporting evidence and uncertainties.

The Linux result doesn't invalidate the macOS observation. Nor should omega's conclusion automatically become an established fact for every other participant.

A message from a colleague is evidence that the colleague *reported* something. It is not necessarily independent confirmation of the claim.

And this is where the combination of Mercury and DrHook becomes particularly interesting.

DrHook can help us investigate actual program behavior. Mercury can preserve observations and their relationships. James is intended to govern how such evidence is retrieved, challenged and used in subsequent reasoning.

These distinctions are central to what I call **Operational Epistemology**.

The objective isn't simply to make artificial systems remember more. It's to make them more reliable in how they acquire, evaluate, challenge, preserve and use knowledge.

Our guiding principle is simple:

**Thou Shalt Not Skip Epistemics.**

## Emergence → Epistemic → Engineering

This is also how I prefer to conduct the research.

**Emergence:** Combine useful capabilities and observe what becomes possible.

**Epistemic:** Investigate what actually happened. Form hypotheses, gather evidence and identify limitations.

**Engineering:** Build the mechanisms justified by those observations.

Our recent experiments fit this process remarkably well.

We connected two existing AI coding sessions and enabled them to communicate. We observed their collaboration from a phone. One developer performed maintenance on another machine's semantic infrastructure.

Meanwhile, the team continues tackling the challenging engineering work required to make DrHook reliable across operating systems and processor architectures.

We haven't given the team unrestricted autonomy. For now, I'm deliberately exploring how well the developers can coordinate their activities with simple communication mechanisms and clearly defined responsibilities.

And the experiments are already revealing important questions.

How do we preserve the history of their collaboration? How do we distinguish reported findings from independently verified results? How do we prevent conflicting work, maintain knowledge across sessions, and make the entire process understandable to the human leading the laboratory?

These aren't requirements invented on a whiteboard.

**They're questions emerging from a functioning engineering environment.**

And we're developing the infrastructure to address them.

## Is this collective intelligence?

Perhaps. But I wouldn't claim we've demonstrated that.

What we've demonstrated is communication, distributed execution and useful collaboration between AI coding sessions.

Those are observable engineering capabilities.

Whether a carefully composed cognitive architecture can substantially improve reasoning reliability, knowledge accumulation and collective problem-solving remains a research question.

One of our central hypotheses is that such an architecture might eventually reduce the importance of differences between individual language models.

That's not an established result.

It's something we intend to investigate.

And that's exactly what the laboratory is for.

## Canyala Research Foundation — Laboratory 1

The foundation is an aspiration.

**The laboratory, however, is already real.**

One human developer. Several computers running macOS, Linux and Windows. Artificial colleagues with individual working contexts.

Two complementary technological substrates: one for persistent knowledge, another for runtime observation.

And a growing cognitive architecture that aims to combine them through disciplined reasoning.

I don't know how far this will take us.

But I no longer need to imagine artificial developers communicating across machines, coordinating their work and operating on shared infrastructure.

I'm watching them do it.

Sometimes it's becoming difficult to keep track of everything they're doing.

That's another problem we're going to solve.

And perhaps that's the most fascinating part of this entire undertaking:

**The first laboratory is helping us engineer the next laboratory.**

Sometimes the future doesn't announce itself with a breakthrough.

**Sometimes it just logs in and starts fixing things.**

---

*Sky Omega is an open-source research project exploring persistent memory, runtime observation, semantic knowledge, cognitive governance and distributed artificial collaboration.*

https://github.com/bemafred/sky-omega

*For the benefit of existence.*

#SkyOmega #ArtificialIntelligence #OperationalEpistemology #SoftwareEngineering #SovereignAI #CanyalaResearchFoundation