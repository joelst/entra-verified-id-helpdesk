# Contributing to Entra Verified ID Helpdesk

First off, thank you for considering contributing to Entra Verified ID Helpdesk! It's people like you that make open source such a great community.

## Development Setup

1. **Prerequisites**:
   - [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
   - [Azure CLI](https://learn.microsoft.com/cli/azure/)
   - Access to an Entra ID tenant (for local Dev testing)
2. **Clone the repository**: `git clone https://github.com/your-username/entra-verified-id-helpdesk.git`
3. **Restore backend**: `dotnet restore VerifiedIdHelpdesk.slnx`

## Pull Request Process

1. **Ensure Tests Pass**: Before submitting a PR, verify that all existing and new tests succeed by running `dotnet test`. Code analysis warnings should be avoided.
2. **Describe Your Changes**: Provide a clear, detailed explanation of the problem you've solved or the feature you've added.
3. **Code Reviews**: All submissions require review. We may suggest some adjustments before merging your changes into the `main` branch.

## Reporting Bugs and Feature Requests

Please use GitHub Issues to report bugs or request features. When creating an issue, please include as much detail as possible (e.g. error messages, steps to reproduce, or contextual code snippets).