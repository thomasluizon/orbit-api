# Orbit API — Frontend Integration Guide

Base URL: `/api`

All authenticated endpoints require `Authorization: Bearer <token>` header.

---

## Authentication

### Register

```
POST /api/auth/register
Content-Type: application/json
```

**Request:**
```json
{
  "name": "John Doe",
  "email": "john@example.com",
  "password": "SecurePass123!"
}
```

**Response (200):**
```json
{
  "userId": "a1b2c3d4-...",
  "message": "Registration successful"
}
```

**Error (400):**
```json
{ "error": "Email is already registered" }
```

### Login

```
POST /api/auth/login
Content-Type: application/json
```

**Request:**
```json
{
  "email": "john@example.com",
  "password": "SecurePass123!"
}
```

**Response (200):**
```json
{
  "userId": "a1b2c3d4-...",
  "token": "eyJhbGci...",
  "name": "John Doe",
  "email": "john@example.com"
}
```

**Error (401):**
```json
{ "error": "Invalid email or password" }
```

---

## Profile

### Get Profile

```
GET /api/profile
Authorization: Bearer <token>
```

**Response (200):**
```json
{
  "name": "John Doe",
  "email": "john@example.com",
  "timeZone": "America/New_York"
}
```

### Set Timezone

```
PUT /api/profile/timezone
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:**
```json
{ "timeZone": "America/New_York" }
```

**Response:** `200 OK`

---

## Habits

### Get All Habits

```
GET /api/habits
Authorization: Bearer <token>
```

**Query Parameters (all optional):**
| Param | Type | Description |
|-------|------|-------------|
| `tags` | string | Comma-separated tag GUIDs |
| `search` | string | Search in title/description |
| `dueDateFrom` | DateOnly | Filter habits due on or after |
| `dueDateTo` | DateOnly | Filter habits due on or before |
| `isCompleted` | bool | Filter by completion status |
| `frequencyUnit` | string | `Daily`, `Weekly`, `Monthly`, or `none` |

**Response (200):**
```json
[
  {
    "id": "guid",
    "title": "Morning Run",
    "description": "Run 5km every morning",
    "frequencyUnit": "Daily",
    "frequencyQuantity": 1,
    "isBadHabit": false,
    "isCompleted": false,
    "dueDate": "2025-01-15",
    "days": ["Monday", "Wednesday", "Friday"],
    "position": 0,
    "createdAtUtc": "2025-01-01T00:00:00Z",
    "children": [
      {
        "id": "guid",
        "title": "Warm-up stretches",
        "description": null,
        "isCompleted": false,
        "dueDate": "2025-01-15",
        "position": 0,
        "children": []
      }
    ],
    "tags": [
      { "id": "guid", "name": "Health", "color": "#22C55E" }
    ]
  }
]
```

### Create Habit

```
POST /api/habits
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:**
```json
{
  "title": "Morning Run",
  "description": "Run 5km every morning",
  "frequencyUnit": "Daily",
  "frequencyQuantity": 1,
  "days": ["Monday", "Wednesday", "Friday"],
  "isBadHabit": false,
  "subHabits": ["Warm-up stretches", "Cool-down"],
  "dueDate": "2025-01-15"
}
```

Only `title` is required. All other fields are optional.

**Response (201):** Returns the new habit GUID.

### Update Habit

```
PUT /api/habits/{id}
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:** Same shape as create (without `subHabits`).

**Response:** `204 No Content`

### Delete Habit

```
DELETE /api/habits/{id}
Authorization: Bearer <token>
```

**Response:** `204 No Content`

### Log Habit

```
POST /api/habits/{id}/log
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:**
```json
{ "note": "Felt great today!" }
```

Body is optional (can send empty `{}`).

**Response (200):**
```json
{ "logId": "guid" }
```

### Get Habit Logs

```
GET /api/habits/{id}/logs
Authorization: Bearer <token>
```

**Response (200):** Array of log entries.

### Get Habit Metrics

```
GET /api/habits/{id}/metrics
Authorization: Bearer <token>
```

**Response (200):** Metrics object (streaks, completion rates, etc.).

### Bulk Create Habits

```
POST /api/habits/bulk
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:**
```json
{
  "habits": [
    {
      "title": "Read",
      "frequencyUnit": "Daily",
      "frequencyQuantity": 1,
      "subHabits": [
        { "title": "Read 10 pages" }
      ]
    }
  ]
}
```

### Bulk Delete Habits

```
DELETE /api/habits/bulk
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:**
```json
{ "habitIds": ["guid1", "guid2"] }
```

### Reorder Habits

```
PUT /api/habits/reorder
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:**
```json
{
  "positions": [
    { "habitId": "guid", "position": 0 },
    { "habitId": "guid", "position": 1 }
  ]
}
```

**Response:** `204 No Content`

### Move Habit Parent

```
PUT /api/habits/{id}/parent
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:**
```json
{ "parentId": "guid-or-null" }
```

Set `parentId` to `null` to make a sub-habit a top-level habit.

**Response:** `204 No Content`

### Create Sub-Habit

```
POST /api/habits/{parentId}/sub-habits
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:**
```json
{
  "title": "Sub-habit title",
  "description": "Optional description"
}
```

**Response (201):**
```json
{ "id": "guid" }
```

---

## Tags

### Get All Tags

```
GET /api/tags
Authorization: Bearer <token>
```

**Response (200):**
```json
[
  { "id": "guid", "name": "Health", "color": "#22C55E" }
]
```

### Create Tag

```
POST /api/tags
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:**
```json
{ "name": "Health", "color": "#22C55E" }
```

Color must be a hex string like `#FF5733`.

**Response (201):** Returns the new tag GUID.

### Delete Tag

```
DELETE /api/tags/{id}
Authorization: Bearer <token>
```

**Response:** `204 No Content`

### Assign Tag to Habit

```
POST /api/habits/{habitId}/tags/{tagId}
Authorization: Bearer <token>
```

**Response:** `200 OK`

### Unassign Tag from Habit

```
DELETE /api/habits/{habitId}/tags/{tagId}
Authorization: Bearer <token>
```

**Response:** `204 No Content`

---

## User Facts

User facts are personal traits/preferences stored about the user, used to personalize AI responses. They can be auto-extracted from chat or manually managed.

### Get All Facts

```
GET /api/user-facts
Authorization: Bearer <token>
```

**Response (200):**
```json
[
  {
    "id": "guid",
    "factText": "User is a morning person",
    "category": "routine",
    "extractedAtUtc": "2025-01-01T10:00:00Z",
    "updatedAtUtc": null
  }
]
```

Facts are sorted newest-first. Soft-deleted facts are excluded.

### Create Fact

```
POST /api/user-facts
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:**
```json
{
  "factText": "I prefer outdoor workouts",
  "category": "preference"
}
```

`category` is optional. Allowed values: `preference`, `routine`, `context`.

Duplicate detection: if an exact match (case-insensitive) already exists, returns 400.

**Response (201):**
```json
{ "id": "guid" }
```

### Update Fact

```
PUT /api/user-facts/{id}
Authorization: Bearer <token>
Content-Type: application/json
```

**Request:**
```json
{
  "factText": "I prefer indoor workouts now",
  "category": "preference"
}
```

**Response:** `204 No Content`

### Delete Fact

```
DELETE /api/user-facts/{id}
Authorization: Bearer <token>
```

Performs a soft delete.

**Response:** `204 No Content`

**Error (404):**
```json
{ "error": "Fact not found." }
```

---

## Chat

The chat endpoint is the AI-powered core of Orbit. Send a message (optionally with an image), and the AI interprets the user's intent, executes actions (create habits, log completions, assign tags), and returns a conversational response.

### Send Message

```
POST /api/chat
Authorization: Bearer <token>
Content-Type: multipart/form-data
```

**Form fields:**
| Field | Type | Required | Description |
|-------|------|----------|-------------|
| `message` | string | Yes | The user's chat message |
| `image` | file | No | An image attachment (max 20MB) |

**Response (200):**
```json
{
  "aiMessage": "Done! I've created your 'Morning Run' habit and logged it for today.",
  "actions": [
    {
      "type": "CreateHabit",
      "status": "Success",
      "entityId": "guid",
      "entityName": "Morning Run",
      "error": null,
      "field": null,
      "suggestedSubHabits": null,
      "conflictWarning": null
    },
    {
      "type": "LogHabit",
      "status": "Success",
      "entityId": "guid",
      "entityName": "Morning Run",
      "error": null,
      "field": null,
      "suggestedSubHabits": null,
      "conflictWarning": null
    }
  ]
}
```

### Action Types

| Type | Description |
|------|-------------|
| `LogHabit` | Marks a habit as completed for today |
| `CreateHabit` | Creates a new habit |
| `AssignTag` | Assigns a tag to a habit |
| `SuggestBreakdown` | AI suggests sub-habits (no DB change, status is `Suggestion`) |

### Action Statuses

| Status | Meaning |
|--------|---------|
| `Success` | Action executed successfully |
| `Failed` | Action failed — check `error` field |
| `Suggestion` | No action taken — `suggestedSubHabits` contains AI proposals |

### Chat Flow

1. Frontend sends `multipart/form-data` with `message` (and optional `image`)
2. Backend loads user context (habits, tags, facts, routine patterns)
3. AI interprets intent and returns a plan (actions + conversational message)
4. Backend executes each action (create, log, assign)
5. Backend saves changes, then asynchronously extracts personal facts from the conversation
6. Response includes the AI message and action results

### Conflict Warnings

When creating a habit, the response may include a `conflictWarning` if the new habit's schedule conflicts with existing routines:

```json
{
  "conflictWarning": {
    "message": "You already have 3 daily habits. Consider spacing them out.",
    "conflictingHabitIds": ["guid1", "guid2"]
  }
}
```

### Fact Extraction

After each chat, the system automatically extracts personal facts from the conversation (e.g., "User prefers morning workouts"). These are stored as user facts and used to personalize future AI responses. The extraction is deduplication-aware — it won't create facts that already exist.

---

## Error Format

All error responses follow the same shape:

```json
{ "error": "Description of what went wrong" }
```

Validation errors from FluentValidation return 400 with details about which fields failed.

---

## Notes

- All IDs are GUIDs
- All timestamps are UTC ISO 8601
- There is no WebSocket/real-time endpoint — all communication is REST request/response
- The JWT token should be stored client-side and sent with every authenticated request
- Soft-deleted entities (habits, facts) are excluded from GET responses
