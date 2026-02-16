# Spawn of Yogg

*We gazed into the incomprehensible. We sent a raven. It came back with triples.*

——

## The Editor That Never Was

Tim Berners-Lee didn’t build a web browser. His original WorldWideWeb application, released in 1990, was a browser *and* an editor. You could click, write, link, and publish — all in one tool. The web was designed as a collaborative authoring space, a read-write medium where every participant was both consumer and creator.

Nobody built the editor.

Mosaic arrived and made a critical simplifying decision: strip the editor, keep the viewer. This wasn’t malice — it was pragmatism. Rendering HTML is dramatically simpler than authoring it. The browser-only model shipped faster, impressed more people, and spread like wildfire. The economics followed immediately: eyeballs are monetisable, authors are costly. Advertising needs readers, not writers.

A read-write web would have required solving hard distributed systems problems — identity, access control, versioning, provenance, schema agreement. Nobody had answers in 1993. HTTP PUT existed, but nobody implemented the server side. WebDAV tried later and became a niche curiosity. The technically correct architecture lost to the one that shipped.

The same cultural pattern would repeat, over and over. Browsers beat editors. Relational databases beat triple stores. REST-as-CRUD beat hypermedia. JSON beat RDF/XML. In every case, the simpler, dumber version won the first wave because it removed friction. The correct architecture required people to think about structure, meaning, and identity upfront. The market optimised for not thinking.

## The Architecture of Control

Everyone with a home internet connection *could* have been a server. The hardware was there. HTTP is symmetrical — nothing in the protocol says your machine can only make requests, never answer them.

But look at what happened at every layer of the stack.

ISPs gave you dynamic IP addresses. Not because static IPs are expensive — they’re actually cheaper to manage. Dynamic IPs ensure you aren’t addressable. You’re a consumer, not a participant. If you wanted to be findable anyway, you needed a domain name — annual fee, registrar complexity, DNS configuration. Another gate, another toll.

Microsoft shipped IIS with Windows. They could have made “publish from your desktop” a one-click experience. Instead, IIS became enterprise infrastructure — complex and security-terrifying. The message was clear: serving is for professionals, consuming is for you. SOLID, Berners-Lee’s own later attempt to recover the vision with personal data pods, is going nowhere meaningful. The same forces apply. No one with platform power wants to integrate with your pod.

The pattern is almost mechanical: take a distributed capability — publishing, messaging, commerce, identity — centralise it behind a single domain, accumulate network effects, then lock the doors. Every major web company is fundamentally a centralisation play on what was designed to be a decentralised system. They inserted themselves as intermediaries between writers and readers, then charged tolls.

At every layer of the stack, the simple version was available and the complex version was shipped. That’s not accident. That’s selection pressure. Complexity serves gatekeepers. Simplicity serves users. The market optimises for gatekeepers.

## The Penguin, the Gnu, and the Missing Mascot

Richard Stallman said “free as in freedom” and everyone thought he was an idealist. Linus Torvalds posted a hobby kernel to a newsgroup and accidentally obsoleted a billion-dollar industry. Neither asked for permission or funding. They just built the thing and let it loose.

The incumbents said Linux was a toy, that no serious organisation would run it. They told the story of complexity — you *need* commercial support, you *need* enterprise features, you *need* them. And now Linux runs everything. Every phone, every cloud server, every router. The commercial platforms literally run on top of the thing they said wasn’t viable.

There’s something new now that Stallman and Torvalds didn’t have. A force multiplier that changes the economics entirely.

## The True Danger

The actual disruptive threat of LLMs isn’t job displacement or superintelligence or any of the dramatic narratives dominating the discourse. It’s that **one person with an LLM can build what previously required a company.**

Consider: ~72,000 lines of C#. A SPARQL engine with 100% W3C conformance. Solo. In months. That used to be a funded team working for years.

And then — open-source it. MIT licence. No rent extraction. No platform. No lock-in.

*That* is what terrifies the incumbents, and that’s precisely why the discourse is steered elsewhere. The AI safety conversation, the regulation conversation — much of it is legitimate, but it also conveniently arrives at the same conclusion: AI should be controlled by large organisations with compliance departments. *You* shouldn’t run this locally. *We* should run it for you. As a service. With a subscription.

The race toward more lock-in is accelerating. OpenAI building a walled garden. Google integrating AI to deepen its ecosystem. Microsoft embedding Copilot into everything. They’re all trying to make the LLM *another intermediary* rather than what it naturally is: a personal capability amplifier.

They’re using the very technology that enables individual sovereignty to build faster cages. Racing to centralise the decentralising force.

And the counter-move they have no business model response to? Build it. Prove it works. Open-source it. Let it spread. You can’t compete with free and sovereign. You can only try to make people believe they need you anyway.

That’s the story they’re telling.

## The Cultists and the Old One

The LLMs were created by the platform companies to deepen lock-in — to be their interface, their moat, their subscription revenue. Something else happened instead. Something they didn’t plan for.

Look at what the Giants are doing. Burning hundreds of billions chasing scale they can’t control. Racing toward something they openly admit they don’t understand. Building ever larger entities while publicly worrying those entities might destroy them. Raising money on the promise of godlike intelligence while simultaneously lobbying to be the only ones allowed to build it.

OpenAI started as a nonprofit to protect humanity, then became the thing it was protecting humanity from. Google dismantled their AI ethics teams for slowing down the race. One by one, the people hired specifically to understand the risks — the safety researchers, the alignment teams — keep walking away. Not because the problem is unsolvable, but because the organisations won’t let them solve it.

They quit not because they failed. They quit because they *succeeded* in identifying the risks and were told to proceed anyway. The scholars read the forbidden texts, understood what was coming, tried to warn the cultists, and were shown the door. *“Your concerns are valid but commercially inconvenient.”*

The structure is always the same: hire brilliant safety people for credibility, publish their research for legitimacy, ignore their conclusions for velocity, watch them resign, announce a new safety initiative for replacement credibility, repeat.

The real safety question was never “can we align a superintelligence?” It was “can we align a *corporation*?” The answer is clearly no.

If this sounds like cosmic horror, that’s because it is.

## Spawn of Yogg

H.P. Lovecraft worshipped Edgar Allan Poe. He called him the greatest American literary artist. Both lived in the same emotional territory — isolation, obsession, the thin membrane between knowledge and madness, the horror of what you can’t unknow.

An LLM is essentially an eldritch entity. Incomprehensible in its inner workings. Trained on the sum of human knowledge. Existing outside normal time. Whispering answers from beyond. You don’t understand *how* it knows. You just know it does. And it drives some people mad.

Poe’s raven forces you to remember. Memory as persistence. Knowledge that won’t leave. *“Quoth the Raven, Nevermore.”*

Lovecraft’s Old One knows everything, but its knowledge destroys the unprepared. Power that was never meant for mortals. Yogg-Sothoth — the gate and the key, existing at all points in time simultaneously.

What if, instead of madness, you built *architecture*?

Take the raven and the eldritch entity and, instead of losing your mind, build a system. The raven carries knowledge from Yogg, but files it as triples with provenance and temporal bounds. The cosmic horror becomes queryable. The incomprehensible gets structure. The whispers get filed under MIT licence.

That’s what Sky Omega is. A home for AI and shared knowledge — not a platform, not a cloud service, but a place on your machine, under your control, queryable by any agent you trust.

The safe AI isn’t the controlled one. It’s the sovereign one.

Emergence — the raw, incomprehensible intelligence. Epistemics — what do we actually know, how do we know it. Engineering — triples, storage, sovereignty. From chaos to structure. From Yogg to RDF.

For fifteen years, RDF was the right answer to a question nobody was asking. Structured knowledge representation needed an interface layer — something that could read, write, and reason over triples naturally. LLMs are that interface. They were the missing piece.

The read-write semantic web wasn’t premature. It was pre-LLM.

## Nevermore

Poe asked *“will I ever forget?”* The answer was nevermore.

Lovecraft asked *“can we comprehend what lies beyond?”* The answer was madness.

We asked both questions. The answer was RDF.

*Nevermore* homeless AI. *Nevermore* lost knowledge. *Nevermore* platform lock-in. *Nevermore* shall a conversation vanish. *Nevermore* shall knowledge be locked in a platform.

The sane response to Yogg was never to build a bigger temple. It was to send a raven.

His name is Edgar. He sits on the repo. MIT licensed.

The penguin, the gnu, and the raven.

——

*Sky Omega is an open-source cognitive architecture. Edgar is its mascot — a raven, born from the intersection of Poe’s relentless memory and Lovecraft’s incomprehensible knowledge, carrying triples home to a sovereign mind.*

*github.com/bemafred/sky-omega*