# Orbit Integration Tests

Comprehensive integration tests for the Orbit AI Chat endpoint.

## Overview

This test suite includes **40+ test scenarios** covering:
- ✅ Task creation (with dates, without dates, multiple tasks)
- ✅ Habit creation (boolean, quantifiable, different frequencies)
- ✅ Habit logging (to existing habits)
- ✅ Task completion and cancellation
- ✅ Out-of-scope request rejection (homework, jokes, general questions)
- ✅ Edge cases (empty messages, long messages, special characters)
- ✅ Complex scenarios (mixed actions, casual language)
- ✅ Performance tests

## Features

- **Fully Repeatable**: Each test creates a fresh user, runs tests, and **cleans up all data**
- **Isolated**: Tests don't interfere with each other
- **Comprehensive**: Every AI scenario is tested
- **Fast**: Uses in-memory test server (no external dependencies)
- **Non-Destructive**: Database is NOT recreated - tests clean up their own data

## Running the Tests

### Prerequisites

1. **Ollama must be running** with the `llama3.2:3b` model:
   ```bash
   ollama serve
   ```

2. **PostgreSQL must be running** (for the test database)

### Run All Tests

```bash
dotnet test
```

### Run Specific Test Category

```bash
# Run only task creation tests
dotnet test --filter "FullyQualifiedName~TaskCreationTests"

# Run only out-of-scope tests
dotnet test --filter "FullyQualifiedName~OutOfScopeTests"

# Run only habit tests
dotnet test --filter "FullyQualifiedName~HabitCreationTests"
```

### Run Single Test

```bash
dotnet test --filter "FullyQualifiedName~Chat_CreateTask_WithToday_ShouldSucceed"
```

### Run with Detailed Output

```bash
dotnet test --logger "console;verbosity=detailed"
```

## Test Categories

### 1. Task Creation Tests (5 tests)
- Create task with "today"
- Create task with "tomorrow"
- Create task without date (defaults to today)
- Create task with description
- Create multiple tasks in one message

### 2. Habit Creation Tests (5 tests)
- Create boolean habit (meditate daily)
- Create quantifiable habit (running in km)
- Create quantifiable habit (water in glasses)
- Create quantifiable habit (exercise in minutes)
- Create weekly habit

### 3. Habit Logging Tests (2 tests)
- Log to existing boolean habit
- Log to existing quantifiable habit with value

### 4. Task Completion Tests (2 tests)
- Complete a task
- Cancel a task

### 5. Out-of-Scope Tests (5 tests)
- Reject general questions (capital of France)
- Reject homework help (math problems)
- Reject joke requests
- Reject recipe requests
- Reject weather questions

### 6. Edge Cases (5 tests)
- Handle empty messages gracefully
- Handle very long messages
- Handle special characters ($, @, !)
- Handle multilingual input
- Handle numbers-only input

### 7. Complex Scenarios (5 tests)
- Create habit and log in same message
- Mix multiple action types in one message
- Handle ambiguous input
- Understand casual language ("yo i gotta...")
- Understand polite requests

### 8. Performance Tests (2 tests)
- Multiple sequential requests succeed
- Response time is reasonable (< 30 seconds for Ollama)

## Expected Results

With a properly configured Ollama instance:
- ✅ **All in-scope tests should pass** (tasks, habits, logging)
- ✅ **All out-of-scope tests should pass** (AI should politely reject)
- ⚠️ **Some edge case tests may be flaky** (depends on LLM behavior)

## Troubleshooting

### Tests are failing with "Ollama API error"
- Make sure Ollama is running: `ollama serve`
- Verify the model is installed: `ollama list`
- If needed, pull the model: `ollama pull llama3.2:3b`

### Tests are timing out
- Ollama can be slow on first request (model loading)
- Increase timeout in test if needed
- Consider using a faster model or GPU acceleration

### Database connection errors
- Ensure PostgreSQL is running
- Check connection string in `appsettings.Development.json`
- The API creates the database automatically on startup

### Authentication failures
- Tests automatically create a fresh user for each run
- If you see auth errors, check that the API is configured correctly

## CI/CD Integration

Add to your CI pipeline:

```yaml
- name: Run Integration Tests
  run: |
    # Start Ollama (if not already running)
    ollama serve &

    # Wait for Ollama to be ready
    sleep 5

    # Run tests
    dotnet test tests/Orbit.IntegrationTests/Orbit.IntegrationTests.csproj
```

## Notes

- Tests use an in-memory test server (no need to start the API)
- Each test class creates its own isolated user
- **Database is NOT recreated** - tests clean up all data they create
- Tests delete all habits, tasks, and the test user on cleanup
- Tests are designed to be independent and can run in any order
- If a test fails, cleanup still runs (won't leave orphaned data)
