# Orbit - AI-Powered Life Management System

Orbit is a modern, cloud-ready life management application that combines habit tracking and task management with AI-driven natural language processing. Built with .NET 10.0 and powered by AI (Gemini or Ollama), Orbit helps users manage their daily lives through conversational interactions.

## 🎯 MVP Features

### Core Authentication & User Management
- **User Registration** - Create new accounts with email and password
- **User Login** - Secure JWT-based authentication
- **Password Hashing** - BCrypt-powered password security
- **Session Management** - JWT tokens with configurable expiration

### AI-Driven Chat Processing
- **Natural Language Understanding** - Process user messages in plain English
- **Intent Recognition** - AI automatically interprets user intentions:
  - Creating tasks from messages
  - Creating habits from messages
  - Logging habit completion
  - Updating task status
- **Multi-Provider AI Support**:
  - **Google Gemini** - Cloud-based, fast (1.6s), highly reliable JSON responses
  - **Ollama** - Local LLM option (phi3.5:3.8b), privacy-focused, no API key required
- **Smart Context Awareness** - AI considers user's active habits and pending tasks in responses
- **Conversational Responses** - AI provides natural, encouraging feedback

### Habit Management
- **Create Habits** - Define habits with:
  - Title and description
  - Type (Boolean or Quantifiable)
  - Flexible frequency (Daily, Weekly, Monthly, Yearly)
  - Frequency quantity (e.g., "every 2 weeks")
  - Optional specific days (Monday-Sunday) for weekly habits
  - Target values and units for quantifiable habits
- **Log Habit Completion** - Record habit progress with optional values
- **Activate/Deactivate Habits** - Pause habits without deletion
- **View Active Habits** - List all current habits with metadata
- **Delete Habits** - Permanently remove habits (cascades habit logs)

### Task Management
- **Create Tasks** - Add tasks with:
  - Title and description
  - Due dates (today, tomorrow, specific dates)
  - Automatic AI-driven due date parsing
- **Update Task Status** - Progress tracking:
  - Pending → In Progress → Completed
  - Cancel tasks
- **View Tasks** - List all tasks or filter by status (active/completed)
- **Delete Tasks** - Remove completed or cancelled tasks

### API Documentation
- **Swagger UI** - Interactive API explorer at `/swagger` (development mode)
- **Complete Endpoint Documentation** - All endpoints documented with request/response schemas
- **JWT Bearer Authentication** - Integrated security in Swagger for testing

## 🏗️ Architecture

Orbit follows **Clean Architecture** principles with clear separation of concerns:

```
src/
├── Orbit.Api/              # API layer - Controllers & HTTP handlers
├── Orbit.Application/      # Application layer - CQRS Commands & Queries
├── Orbit.Infrastructure/   # Infrastructure - Database, Services, Configuration
└── Orbit.Domain/          # Domain layer - Entities, Interfaces, Business Logic

tests/
└── Orbit.IntegrationTests/ # Comprehensive integration tests (15+ scenarios)
```

### Technology Stack
- **.NET 10.0** - Latest .NET runtime
- **C# 13** - Modern language features
- **PostgreSQL** - Robust relational database
- **Entity Framework Core 10** - ORM for data access
- **MediatR 14** - CQRS pattern implementation
- **JWT Bearer** - Secure token-based authentication
- **Npgsql 10** - PostgreSQL client driver
- **xUnit & FluentAssertions** - Testing framework

### Design Patterns
- **Clean Architecture** - Layered architecture with dependency inversion
- **CQRS** - Command Query Responsibility Segregation via MediatR
- **Repository Pattern** - Generic repository with Unit of Work
- **Result Pattern** - Type-safe error handling without exceptions
- **Factory Methods** - Domain entities use factory methods for creation
- **Dependency Injection** - Full DI container configuration

## 🚀 Getting Started

### Prerequisites
- .NET 10.0 SDK
- PostgreSQL 12+ (or Docker)
- (Optional) Ollama installed locally if using Ollama provider
- (Optional) Google Gemini API key for Gemini provider

### Database Setup

1. **PostgreSQL Connection String**
   Add to `appsettings.Development.json`:
   ```json
   {
     "ConnectionStrings": {
       "DefaultConnection": "Host=localhost;Port=5432;Database=orbit_dev;Username=postgres;Password=your_password"
     }
   }
   ```

2. **Database Initialization**
   The application uses `Database.EnsureCreatedAsync()` for MVP - it automatically creates the schema on first run.

### Configuration

Create `appsettings.Development.json` in `src/Orbit.Api/`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Port=5432;Database=orbit_dev;Username=postgres;Password=your_password"
  },
  "JwtSettings": {
    "SecretKey": "your-secret-key-at-least-32-characters-long",
    "Issuer": "Orbit",
    "Audience": "OrbitUsers",
    "ExpirationMinutes": 1440
  },
  "AiProvider": "Ollama",
  "OllamaSettings": {
    "BaseUrl": "http://localhost:11434"
  },
  "GeminiSettings": {
    "ApiKey": "your-gemini-api-key",
    "ModelId": "gemini-2.5-flash"
  }
}
```

### Running the Application

```bash
# Build the solution
dotnet build Orbit.slnx

# Run the API
cd src/Orbit.Api
dotnet run

# Application starts at https://localhost:7174
# Swagger UI available at https://localhost:7174/swagger
```

### Running Tests

```bash
# Run all integration tests
dotnet test tests/Orbit.IntegrationTests/Orbit.IntegrationTests.csproj

# Run with verbose output
dotnet test tests/Orbit.IntegrationTests/Orbit.IntegrationTests.csproj -v detailed
```

## 📚 API Endpoints

### Authentication
- `POST /api/auth/register` - Register new user
  - Body: `{ name, email, password }`
  - Returns: `{ userId, token }`

- `POST /api/auth/login` - Login user
  - Body: `{ email, password }`
  - Returns: `{ userId, token }`

### Chat (AI-Driven)
- `POST /api/chat` - Send message to AI
  - Body: `{ message }`
  - Returns: `{ executedActions, aiMessage }`
  - **Protected**: Requires JWT token
  - **Executes**: Actions determined by AI (CreateTask, CreateHabit, LogHabit, UpdateTask)

### Habits
- `GET /api/habits` - List active habits
  - Returns: List of habit objects with metadata
  - **Protected**: Requires JWT token

- `POST /api/habits` - Create new habit
  - Body: `{ title, frequencyUnit, frequencyQuantity, type, description, unit, targetValue, days }`
  - Returns: Created habit object
  - **Protected**: Requires JWT token

- `POST /api/habits/{id}/log` - Log habit completion
  - Body: `{ date, value }`
  - Returns: Executed action summary
  - **Protected**: Requires JWT token

- `DELETE /api/habits/{id}` - Delete habit
  - **Protected**: Requires JWT token

### Tasks
- `GET /api/tasks` - List tasks
  - Query: `?includeCompleted=true/false` (default: active only)
  - Returns: List of task objects with status
  - **Protected**: Requires JWT token

- `POST /api/tasks` - Create new task
  - Body: `{ title, description, dueDate }`
  - Returns: Created task object
  - **Protected**: Requires JWT token

- `PUT /api/tasks/{id}/status` - Update task status
  - Body: `{ status }` (Pending, InProgress, Completed, Cancelled)
  - Returns: Updated task object
  - **Protected**: Requires JWT token

- `DELETE /api/tasks/{id}` - Delete task
  - **Protected**: Requires JWT token

## 🤖 AI Integration

### Chat Processing Workflow
1. User sends a natural language message via `/api/chat`
2. System retrieves user's active habits and pending tasks for context
3. Message is sent to configured AI provider (Gemini or Ollama) with system prompt
4. AI analyzes the message and returns a structured action plan (JSON)
5. Each action is executed (create habit, log habit, create task, etc.)
6. Results and AI response are returned to user

### System Prompt
The AI system prompt includes:
- Clear role definition (personal assistant and life manager)
- User's active habits (for context-aware suggestions)
- User's pending tasks (for task-aware responses)
- Strict JSON schema for reliable action parsing
- Rules for valid frequencies, task dates, and habit properties

### Supported AI Providers

**Google Gemini (Recommended)**
- Model: `gemini-2.5-flash`
- Response Time: ~1.6 seconds
- Reliability: ~95% (highly consistent JSON)
- Cost: Free tier (15 RPM limit), paid plans available
- Setup: Requires API key from [Google AI Studio](https://aistudio.google.com)

**Ollama (Local Alternative)**
- Model: `phi3.5:3.8b`
- Response Time: ~30 seconds
- Reliability: ~65% (inconsistent JSON formatting)
- Cost: Free (runs locally)
- Setup: Run `ollama serve` before starting the API
- Privacy: All processing happens locally

## 📊 Domain Model

### User
- Unique user accounts with email-based authentication
- Password stored as BCrypt hash
- Creation timestamp tracking

### Habit
- **Properties**: Title, description, frequency, type, unit, target value, status
- **Types**:
  - **Boolean**: Simple yes/no habits (e.g., "Meditated today?")
  - **Quantifiable**: Value-based habits (e.g., "Drank 8 glasses of water")
- **Frequency**: Flexible system supporting:
  - Base units: Day, Week, Month, Year
  - Quantity: Integer multiplier (1, 2, 3, etc.)
  - Example: "Every 2 weeks" = Week + 2
  - Optional specific days (Monday-Sunday) for weekly habits
- **Active Status**: Can be activated/deactivated without deletion
- **Logs**: Collection of completion records with dates and values

### HabitLog
- Individual completion records for habits
- Date and value (for quantifiable habits)
- Indexed for efficient querying

### TaskItem
- **Properties**: Title, description, due date, status
- **Status Options**:
  - Pending - Not yet started
  - InProgress - Currently being worked on
  - Completed - Finished successfully
  - Cancelled - No longer needed
- **Dates**: Support for relative dates (today, tomorrow) and specific dates
- **Indexing**: Optimized queries by user and status

## 🧪 Testing

The project includes comprehensive integration tests covering:

### Test Coverage
- **Task Creation**: Creating tasks with AI-parsed dates (today, tomorrow, specific dates)
- **Habit Creation**: Both boolean and quantifiable habits with frequencies
- **Habit Logging**: Recording habit completion with optional values
- **Complex Scenarios**: Multi-action requests, edge cases
- **Out-of-Scope Handling**: Gracefully rejecting invalid requests
- **Error Handling**: Proper error responses and status codes

### Test Infrastructure
- **Repeatable Tests**: Each test creates a unique user and cleans up after itself
- **Proper Isolation**: No test dependencies or shared state
- **Rate Limiting**: Respects Gemini API rate limits (15 RPM)
- **Provider Support**: Works with both Gemini and Ollama for testing

### Running Tests
```bash
# Run all tests
dotnet test

# Run specific test class
dotnet test --filter="FullyQualifiedName~AiChatIntegrationTests"

# Run single test
dotnet test --filter="Name=Chat_CreateTask_WithToday_ShouldSucceed"
```

## 🎨 Features Deep Dive

### Flexible Habit Frequency System
Instead of fixed options (daily, weekly, etc.), Orbit uses a flexible two-component system:

**FrequencyUnit** (the base unit)
- Day: Track daily habits
- Week: Track weekly habits
- Month: Track monthly habits
- Year: Track yearly habits

**FrequencyQuantity** (the multiplier)
- 1 = every unit (daily = every day, weekly = every week)
- 2+ = every N units (every 2 weeks, every 3 months, etc.)

**Examples**
- Morning run: Day + 1 = Every day
- Team meeting: Week + 1 + [Monday, Wednesday] = Every Monday and Wednesday
- Maintenance task: Month + 2 = Every 2 months
- Annual review: Year + 1 = Every year

### Boolean vs Quantifiable Habits
**Boolean Habits** (Yes/No)
- Examples: "Did I meditate?", "Flossed teeth?"
- Logged as: True/False (stored as 1/0)
- No unit or target needed
- Can only be logged once per day

**Quantifiable Habits** (Numeric)
- Examples: "Glasses of water", "Minutes of exercise", "Pages read"
- Require: Unit (string) and optional Target Value (decimal)
- Logged with: Numeric value
- Can be logged multiple times per day

### AI Action Types
The AI can execute four types of actions:

1. **CreateHabit**: Parse user message and create a new habit
   - Extracts: Title, frequency, type, unit, target
   - Validates: Required fields and business rules

2. **LogHabit**: Record completion for an existing habit
   - Finds: Matching active habit
   - Records: Date and value

3. **CreateTask**: Parse user message and create a task
   - Extracts: Title, description, due date
   - Handles: Relative dates (today, tomorrow)

4. **UpdateTask**: Change task status
   - Updates: Status to InProgress, Completed, or Cancelled
   - Finds: Matching task by description/context

## 📝 Development Guidelines

### Code Structure
- **Domain Layer**: Pure business logic, no frameworks
- **Application Layer**: CQRS handlers, orchestration, validation
- **Infrastructure Layer**: Database, external services, configuration
- **API Layer**: HTTP contracts, authentication, response formatting

### Adding New Features
1. Define domain entities and business rules
2. Create command/query handlers in Application layer
3. Implement repository methods if needed
4. Add API controller endpoint
5. Write integration tests
6. Update API documentation

### Error Handling
The project uses the Result pattern for errors:
```csharp
// Success case
var result = await command.Execute();
if (result.IsSuccess) {
    return Ok(result.Value);
}

// Error case
return BadRequest(new { error = result.Error });
```

## 🐛 Known Limitations (MVP)

- **No Migrations Yet**: Uses `EnsureCreatedAsync()` instead of EF Core migrations
- **No Email Verification**: User registration doesn't require email confirmation
- **No Password Reset**: No password recovery mechanism
- **No User Profiles**: Can't update user information after registration
- **No Habit Analytics**: No analytics, charts, or progress tracking UI
- **No Notifications**: No email or push notifications
- **No Mobile App**: Web API only
- **No Rate Limiting**: API has no rate limiting (ready for production addition)
- **No Logging**: Minimal structured logging (ready for Serilog addition)

## 🚦 Next Steps (Post-MVP)

- [ ] Database migrations and version control
- [ ] Email verification and password recovery
- [ ] User profile management and settings
- [ ] Habit analytics and charts
- [ ] Progress notifications and reminders
- [ ] Mobile app (React Native or Flutter)
- [ ] Advanced filtering and search
- [ ] Habit templates and categories
- [ ] Social sharing and challenges
- [ ] Dark mode and UI customization
- [ ] Offline-first mobile experience
- [ ] Voice input for chat
- [ ] Advanced AI insights and recommendations

## 📄 License

[Specify your license here]

## 🤝 Contributing

Contributions are welcome! Please follow the established architecture patterns and include tests for new features.

## 📞 Support

For issues or questions, please open an issue on the project repository.

---

**Current Status**: MVP with core features complete and integration tested. Ready for deployment and user testing.
