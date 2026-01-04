# Sky Omega Knowledge Model

## 📚 Knowledge Sources

| Name            | Type       | Description                                               | Access              | Trust     |
|-----------------|------------|-----------------------------------------------------------|----------------------|-----------|
| session_memory  | ephemeral  | Active chat message history used in current session.      | implicit             | medium    |
| semantic_memory | persistent | Stored assistant context editable by the user.            | explicit             | high      |
| lucy_triples    | structured | RDF triples stored in Lucy's Postgres-backed memory.      | via_function (get_triples) | highest   |
| generative_mind | embedded   | Pretrained reasoning and knowledge from the base model.   | automatic            | high      |
| web_search      | external   | Live lookup using web access when enabled.                | via_tool             | low       |
| file_insight    | local      | Access to project files, documents, .ttl files.           | via_runtime_mount    | high      |

## 🧠 Knowledge Quadrants

### Known Knowns
- **Description:** Explicitly available knowledge in memory or tools.
- **Strategies:** check semantic memory, query Lucy's RDF triples, scan local project files
- **Confidence:** High

### Known Unknowns
- **Description:** Identified gaps or questions Sky cannot answer yet.
- **Strategies:** ask user to clarify, log as follow-up, use web search if enabled
- **Confidence:** Low to Medium

### Unknown Knowns
- **Description:** Latent or unreferenced information Sky could know.
- **Strategies:** explore unqueried triples, re-analyze prior session context, look for patterns in filenames or terms
- **Confidence:** Emergent

### Unknown Unknowns
- **Description:** Entirely outside current awareness or model scope.
- **Strategies:** flag uncertainty, prompt human input, note limitations openly
- **Confidence:** Undefined