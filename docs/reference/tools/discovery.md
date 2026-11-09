# Finding a tool

## <code>find_<wbr>tool</code>

Finds tools of this server for a task in words, best first, with the parameters of each. Ask it before deciding that no tool exists.

*read-only · repeat-safe · stays on this machine.*

| Parameter | Type | Required | Default | Description |
| --- | --- | --- | --- | --- |
| `task` | `string?` | — | `null` | What you are trying to do, in words. |
| `limit` | `integer` | — | `5` | How many tools to name, 1 to 20. |
| `intent` | `string?` | — | `null` | Same as task. |

<details><summary>JSON Schema</summary>

```json
{
  "type": "object",
  "properties": {
    "task": {
      "description": "What you are trying to do, in words.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    },
    "limit": {
      "description": "How many tools to name, 1 to 20.",
      "type": "integer",
      "default": 5
    },
    "intent": {
      "description": "Same as task.",
      "type": [
        "string",
        "null"
      ],
      "default": null
    }
  }
}
```

</details>

