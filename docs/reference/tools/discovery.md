# Finding a tool

## <code>find_<wbr>tool</code>

Which tool does a thing, said in your own words — "draw a chart from this table", "check the node
before I try again". Answers with the tools that match: the name to call and every argument each of
them takes, with what it means. When nothing matches it answers with all of them, because this
server has few enough tools that the whole list is a better answer than a wrong one.

*read-only · repeat-safe · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `task` | `string` | yes | — | What you are trying to do, in the words you would use to describe it. |
| `limit` | `integer` | — | `5` | How many tools to return at most; 5 by default, and never more than this server has. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "task": {
      "description": "What you are trying to do, in the words you would use to describe it.",
      "type": "string"
    },
    "limit": {
      "description": "How many tools to return at most; 5 by default, and never more than this server has.",
      "type": "integer",
      "default": 5
    }
  },
  "required": [
    "task"
  ]
}
```

</details>

