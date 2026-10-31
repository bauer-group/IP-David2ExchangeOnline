// SPDX-License-Identifier: MIT
using David2Exchange.Domain;

namespace David2Exchange.Writer;

public interface IProbeWriter
{
    void Write(ProbeReport report);
}
