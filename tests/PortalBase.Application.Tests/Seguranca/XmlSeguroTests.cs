using System.Xml;
using FluentAssertions;
using PortalBase.Application.Seguranca;

namespace PortalBase.Application.Tests.Seguranca;

/// <summary>XML seguro: XXE bloqueado, DTD proibido (Prompt 07, seção 17).</summary>
public sealed class XmlSeguroTests
{
    [Fact]
    public void Xxe_Com_Entidade_Externa_E_Bloqueado()
    {
        const string malicioso =
            "<?xml version=\"1.0\"?>" +
            "<!DOCTYPE root [ <!ENTITY xxe SYSTEM \"file:///etc/passwd\"> ]>" +
            "<root>&xxe;</root>";

        using var leitor = XmlSeguro.CriarLeitor(new StringReader(malicioso));

        var acao = () =>
        {
            while (leitor.Read())
            {
                // Consumir todo o documento — o DTD deve ser rejeitado antes de resolver a entidade.
            }
        };

        acao.Should().Throw<XmlException>(); // DtdProcessing.Prohibit
    }

    [Fact]
    public void Xml_Bem_Formado_Sem_Dtd_E_Lido()
    {
        using var leitor = XmlSeguro.CriarLeitor(new StringReader("<a><b>1</b></a>"));

        var elementos = 0;
        while (leitor.Read())
        {
            if (leitor.NodeType == XmlNodeType.Element)
            {
                elementos++;
            }
        }

        elementos.Should().Be(2);
    }
}
