// SPDX-License-Identifier: MIT
namespace David2Exchange.Output;

public sealed record EwsOutputOptions(
    string TargetUpn,
    string EwsUrl,
    string TenantId,
    string ClientId,
    string CertificateThumbprint,
    string FolderName);
