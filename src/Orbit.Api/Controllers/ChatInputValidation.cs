using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Orbit.Api.Extensions;
using Orbit.Application.Chat.Commands;
using Orbit.Application.Chat.Validators;
using Orbit.Application.Common;

namespace Orbit.Api.Controllers;

internal static class ChatInputValidation
{
    public static async Task<IActionResult?> ValidateAsync(ProcessUserChatCommand command, CancellationToken ct)
    {
        var result = await new ProcessUserChatCommandValidator().ValidateAsync(command, ct);
        var failure = result.Errors.FirstOrDefault(error => error.ErrorCode is ErrorCodes.MessageTooLong
            or ErrorCodes.ChatHistoryTooLarge or ErrorCodes.InvalidChatHistory);
        if (failure is not null)
        {
            var error = failure.ErrorCode switch
            {
                ErrorCodes.MessageTooLong => ErrorMessages.MessageTooLong.Format(AppConstants.MaxChatMessageLength),
                ErrorCodes.ChatHistoryTooLarge => ErrorMessages.ChatHistoryTooLarge,
                _ => ErrorMessages.InvalidChatHistory
            };
            return new BadRequestObjectResult(error.ToErrorBody());
        }
        if (!result.IsValid)
            throw new ValidationException(result.Errors);
        return null;
    }
}
