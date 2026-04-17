PREFIX ex: <http://example.org/>
PREFIX xsd: <http://www.w3.org/2001/XMLSchema#>

INSERT DATA {
  GRAPH <http://example.org/g> {
    ex:s ex:date "2026-04-17"^^xsd:date ;
         ex:topic "first" .
  }
}
