---
id: sql-worker
title: SQL Worker
sidebar_position: 15
description: Pre-built SQL execution worker for running database queries and stored procedures.
---

The SQL Worker is a powerful, multi-database worker that can execute SQL queries and stored procedures against PostgreSQL, SQL Server, and MySQL databases. It uses Dapper for high-performance database operations with full parameterized query support.

### Features

- Multi-database support (PostgreSQL, SQL Server, MySQL)
- Parameterized queries (SQL injection protection)
- Stored procedure execution
- Transaction support with configurable isolation levels
- Three query types (NonQuery, Scalar, Reader)
- Connection pooling via named connections
- Result set limiting
- Automatic JSON result formatting

### Use Cases

| Scenario | Example |
|----------|---------|
| **Scheduled Reports** | Generate daily/weekly reports from database |
| **Data Cleanup** | Periodic cleanup of old records |
| **Data Aggregation** | Calculate statistics and store results |
| **Batch Processing** | Process queued records in batches |
| **Database Maintenance** | Run maintenance scripts on schedule |
| **ETL Jobs** | Extract, transform, load operations |

### Security Model

For security, database connection strings are **never** included in job data. Instead:

1. **Worker Configuration**: Connection strings are configured in the worker's `appsettings.json`
2. **Job Data**: Jobs reference connections by alias name only
3. **UI Integration**: Available connection names appear as dropdown options in the UI

```
???????????????????????????????????????????????????????????
?  Worker appsettings.json                                ?
?  ?????????????????????????????????????????????????????  ?
?  ?  "ExecutorConfig": {                              ?  ?
?  ?    "Connections": {                               ?  ?
?  ?      "MainDatabase": {                            ?  ?
?  ?        "ConnectionString": "Server=...;Pass=...", ?  ?  ? Secrets stay here
?  ?        "Provider": "PostgreSql"                   ?  ?
?  ?      }                                            ?  ?
?  ?    }                                              ?  ?
?  ?  }                                                ?  ?
?  ?????????????????????????????????????????????????????  ?
???????????????????????????????????????????????????????????
                          ?
                          ? Only alias is used
                          ?
???????????????????????????????????????????????????????????
?  Job Data (from API/UI)                                 ?
?  ?????????????????????????????????????????????????????  ?
?  ?  {                                                ?  ?
?  ?    "connectionName": "MainDatabase",  ? Alias     ?  ?
?  ?    "query": "SELECT * FROM Users WHERE Id = @Id", ?  ?
?  ?    "parameters": { "Id": 123 }                    ?  ?
?  ?  }                                                ?  ?
?  ?????????????????????????????????????????????????????  ?
???????????????????????????????????????????????????????????
```

### Worker Configuration

Configure database connections in the worker's `appsettings.json`:

```json
{
  "ExecutorConfig": {
    "Connections": {
      "MainDatabase": {
        "ConnectionString": "Host=localhost;Port=5432;Database=mydb;Username=user;Password=secret;",
        "Provider": "PostgreSql",
        "DefaultTimeoutSeconds": 30
      },
      "ReportingDatabase": {
        "ConnectionString": "Host=reporting-db;Port=5432;Database=reports;Username=report_user;Password=report_pass;",
        "Provider": "PostgreSql",
        "DefaultTimeoutSeconds": 60
      },
      "LegacySqlServer": {
        "ConnectionString": "Server=sql-server;Database=LegacyDb;User Id=sa;Password=pass;TrustServerCertificate=true;",
        "Provider": "SqlServer",
        "DefaultTimeoutSeconds": 30
      },
      "MySqlDatabase": {
        "ConnectionString": "Server=mysql-host;Database=mydb;User=root;Password=pass;",
        "Provider": "MySql",
        "DefaultTimeoutSeconds": 30
      }
    }
  }
}
```

#### Connection Properties

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| `ConnectionString` | string | ? | - | Database connection string |
| `Provider` | enum | ? | - | Database provider: `SqlServer`, `PostgreSql`, `MySql` |
| `DefaultTimeoutSeconds` | number | - | `30` | Default command timeout for this connection |

### Job Data Schema

When creating a job with the SQL Worker, provide the query configuration through Job Data JSON:

```json
{
  "connectionName": "MainDatabase",
  "query": "SELECT * FROM Users WHERE Status = @Status AND CreatedAt > @Since",
  "parameters": {
    "Status": "Active",
    "Since": "2024-01-01"
  },
  "commandType": "Text",
  "queryType": "Reader",
  "timeoutSeconds": 60,
  "maxRows": 1000
}
```

### Configuration Reference

#### Main Properties

| Property | Type | Required | Default | Description |
|----------|------|----------|---------|-------------|
| `connectionName` | string | ? | - | Connection alias from worker configuration |
| `query` | string | ? | - | SQL query or stored procedure name |
| `parameters` | object | - | - | Query parameters as key-value pairs |
| `commandType` | enum | - | `Text` | Command type: `Text` or `StoredProcedure` |
| `queryType` | enum | - | `NonQuery` | Query type: `NonQuery`, `Scalar`, or `Reader` |
| `timeoutSeconds` | number | - | `0` | Command timeout (0 = use connection default) |
| `useTransaction` | boolean | - | `false` | Wrap execution in a transaction |
| `isolationLevel` | enum | - | `ReadCommitted` | Transaction isolation level |
| `maxRows` | number | - | `0` | Maximum rows to return (0 = unlimited) |

#### Query Types

| Type | Description | Result Format |
|------|-------------|---------------|
| `NonQuery` | INSERT, UPDATE, DELETE statements | `{ "affectedRows": 5 }` |
| `Scalar` | Returns first column of first row | `{ "value": 42 }` |
| `Reader` | Returns full result set as JSON array | `{ "data": [...], "rowCount": 10 }` |

#### Command Types

| Type | Description | Example |
|------|-------------|---------|
| `Text` | Raw SQL query (default) | `SELECT * FROM Users` |
| `StoredProcedure` | Execute stored procedure | `sp_GetUserById` |

#### Transaction Isolation Levels

| Level | Description |
|-------|-------------|
| `ReadUncommitted` | Allows dirty reads |
| `ReadCommitted` | Default; prevents dirty reads |
| `RepeatableRead` | Prevents non-repeatable reads |
| `Serializable` | Highest isolation; prevents phantom reads |
| `Snapshot` | Row versioning (SQL Server specific) |

### Practical Examples

#### Example 1: Simple SELECT Query

```json
{
  "connectionName": "MainDatabase",
  "query": "SELECT Id, Name, Email FROM Users WHERE IsActive = true",
  "queryType": "Reader",
  "maxRows": 100
}
```

**Result:**
```json
{
  "queryType": "Reader",
  "rowCount": 3,
  "data": [
    { "id": 1, "name": "John Doe", "email": "john@example.com" },
    { "id": 2, "name": "Jane Smith", "email": "jane@example.com" },
    { "id": 3, "name": "Bob Wilson", "email": "bob@example.com" }
  ],
  "success": true
}
```

#### Example 2: Parameterized INSERT

```json
{
  "connectionName": "MainDatabase",
  "query": "INSERT INTO AuditLogs (Action, UserId, Timestamp, Details) VALUES (@Action, @UserId, @Timestamp, @Details)",
  "parameters": {
    "Action": "UserLogin",
    "UserId": 123,
    "Timestamp": "2024-01-15T10:30:00Z",
    "Details": "Login from IP 192.168.1.100"
  },
  "queryType": "NonQuery"
}
```

**Result:**
```json
{
  "queryType": "NonQuery",
  "affectedRows": 1,
  "success": true
}
```

#### Example 3: Scalar Query (Count)

```json
{
  "connectionName": "ReportingDatabase",
  "query": "SELECT COUNT(*) FROM Orders WHERE OrderDate >= @StartDate AND OrderDate < @EndDate",
  "parameters": {
    "StartDate": "2024-01-01",
    "EndDate": "2024-02-01"
  },
  "queryType": "Scalar"
}
```

**Result:**
```json
{
  "queryType": "Scalar",
  "value": 1547,
  "success": true
}
```

#### Example 4: Stored Procedure

```json
{
  "connectionName": "LegacySqlServer",
  "query": "sp_GenerateMonthlyReport",
  "commandType": "StoredProcedure",
  "parameters": {
    "Year": 2024,
    "Month": 1,
    "DepartmentId": 5
  },
  "queryType": "Reader",
  "timeoutSeconds": 120
}
```

#### Example 5: Transaction with UPDATE

```json
{
  "connectionName": "MainDatabase",
  "query": "UPDATE Accounts SET Balance = Balance - @Amount WHERE AccountId = @FromAccount; UPDATE Accounts SET Balance = Balance + @Amount WHERE AccountId = @ToAccount;",
  "parameters": {
    "Amount": 100.00,
    "FromAccount": "ACC-001",
    "ToAccount": "ACC-002"
  },
  "queryType": "NonQuery",
  "useTransaction": true,
  "isolationLevel": "Serializable"
}
```

#### Example 6: Batch Delete with Limit

```json
{
  "connectionName": "MainDatabase",
  "query": "DELETE FROM TempData WHERE CreatedAt < @CutoffDate LIMIT 10000",
  "parameters": {
    "CutoffDate": "2023-01-01"
  },
  "queryType": "NonQuery",
  "timeoutSeconds": 300
}
```

#### Example 7: PostgreSQL-Specific (RETURNING)

```json
{
  "connectionName": "MainDatabase",
  "query": "INSERT INTO Users (Name, Email) VALUES (@Name, @Email) RETURNING Id, CreatedAt",
  "parameters": {
    "Name": "New User",
    "Email": "newuser@example.com"
  },
  "queryType": "Reader"
}
```

**Result:**
```json
{
  "queryType": "Reader",
  "rowCount": 1,
  "data": [
    { "id": 42, "createdAt": "2024-01-15T10:30:00Z" }
  ],
  "success": true
}
```

### Error Handling

The SQL Worker distinguishes between **permanent** and **transient** errors:

#### Permanent Errors (No Retry)

These errors go directly to the Dead Letter Queue (DLQ):

| Error | Description |
|-------|-------------|
| Syntax Error | Invalid SQL syntax |
| Permission Denied | Insufficient database permissions |
| Object Not Found | Table, column, or procedure doesn't exist |
| Constraint Violation | Unique, foreign key, or check constraint |
| Invalid Connection Name | Connection alias not found in configuration |
| Missing Required Fields | connectionName or query not provided |

#### Transient Errors (Retryable)

These errors are retried according to the job consumer's retry policy:

| Error | Description |
|-------|-------------|
| Connection Timeout | Database connection timeout |
| Command Timeout | Query execution timeout |
| Connection Lost | Network interruption during execution |
| Deadlock | Transaction deadlock (database will retry) |
| Resource Busy | Database server overloaded |

### Job Result

After successful execution, the job stores a result based on query type:

#### NonQuery Result
```json
{
  "queryType": "NonQuery",
  "affectedRows": 5,
  "success": true
}
```

#### Scalar Result
```json
{
  "queryType": "Scalar",
  "value": 42,
  "success": true
}
```

#### Reader Result
```json
{
  "queryType": "Reader",
  "rowCount": 10,
  "data": [
    { "column1": "value1", "column2": 123 },
    ...
  ],
  "success": true
}
```

### Deployment

The SQL Worker can be deployed as a Docker container:

```yaml
# docker-compose.yml
services:
  sql-worker:
    image: milvasoft/milvaion-sql-worker:latest
    environment:
      - Worker__WorkerId=sql-worker-01
      - Worker__RabbitMQ__Host=rabbitmq
      - Worker__RabbitMQ__Port=5672
      - Worker__RabbitMQ__Username=guest
      - Worker__RabbitMQ__Password=guest
      - Worker__MaxParallelJobs=16
      # Connection strings should use secrets in production
      - ExecutorConfig__Connections__MainDatabase__ConnectionString=Host=postgres;Database=mydb;Username=user;Password=secret
      - ExecutorConfig__Connections__MainDatabase__Provider=PostgreSql
```

> **Security Note:** In production, use Docker secrets or a secrets manager (Azure Key Vault, AWS Secrets Manager, HashiCorp Vault) for connection strings.

### Best Practices

1. **Use Parameterized Queries**
   - Never concatenate user input into SQL strings
   - Always use the `parameters` object for dynamic values

2. **Set Appropriate Timeouts**
   - Match timeout to expected query duration
   - Long-running reports should have higher timeouts

3. **Limit Result Sets**
   - Use `maxRows` to prevent memory issues with large datasets
   - Consider pagination for large data exports

4. **Use Transactions Wisely**
   - Enable transactions for multi-statement operations
   - Choose appropriate isolation level for your use case

5. **Monitor Execution**
   - Check execution logs for slow queries
   - Set up alerts for repeated failures

6. **Connection Management**
   - Define separate connections for different workloads (OLTP vs. reporting)
   - Use read replicas for heavy read operations

### Supported Databases

| Database | Provider | Connection String Format |
|----------|----------|-------------------------|
| PostgreSQL | `PostgreSql` | `Host=host;Port=5432;Database=db;Username=user;Password=pass;` |
| SQL Server | `SqlServer` | `Server=server;Database=db;User Id=user;Password=pass;TrustServerCertificate=true;` |
| MySQL/MariaDB | `MySql` | `Server=host;Database=db;User=user;Password=pass;` |

---

*For custom workers, see [Your First Worker](04-your-first-worker.md) and [Implementing Jobs](05-implementing-jobs.md).*
