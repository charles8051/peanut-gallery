# Service Spec: [Service Name]

## Status
**Draft**

| Field        | Value        |
|--------------|--------------|
| Author       | [your name]  |
| Created      | [YYYY-MM-DD]  |
| Last Updated | [YYYY-MM-DD]  |
| Service Type | <!-- Reviewer / Engine / BackgroundService / Transport / Infrastructure --> |

## Purpose
<!-- 1-3 sentences. What does this service do? Which shell owns it? -->

## Location
| Item           | Value                          |
|----------------|--------------------------------|
| Interface      | `[Namespace].[IServiceName]`   |
| Implementation | `[Namespace].[ServiceName]`    |
| Lives in shell | <!-- Cli / Engine / Server / Desktop --> |

> The pure core has no services. Anything with IO, a clock, a `Task`, or an
> `IChatClient` is a shell service and belongs here, not in `PeanutGallery.Core`
> (ADR-0001).

## Dependencies
| Dependency | Type | Notes |
|------------|------|-------|
| `[IDependency]` | port / client | [why it's needed] |

## Key operations
### [MethodName]([parameters])
**Purpose:** [one sentence]
**Input:** [parameters + constraints]
**Output:** `[return type]`
**Error conditions:**
| Condition | Result |
|-----------|--------|

## Configuration
| Key | Type | Default | Description |
|-----|------|---------|-------------|

## Secrets
<!-- Which env vars / secret-store keys. NEVER committed config. -->

## Testing notes
<!-- Unit vs integration, what to fake (the IReviewer port is the seam), key edges. -->
