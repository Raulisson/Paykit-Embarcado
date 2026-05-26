using System;
using System.Windows.Forms;
using System.Diagnostics;
namespace PaykitTotem.Paykit;

public sealed class GerenciadorTotem : IGerenciadorSemTelas
{
    // Injetar JS no WebView
    private readonly Func<string, System.Threading.Tasks.Task> _execJs;

    public GerenciadorTotem(Func<string, System.Threading.Tasks.Task> execJs)
        => _execJs = execJs;

    // Envia evento para o JS
    private void Send(string tipo, string dados = "")
    {
        // Escapa
        dados = dados
            .Replace("\\", "\\\\")
            .Replace("'", "\\'")
            .Replace("\r", "")
            .Replace("\n", "\\n");

        var form = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        form?.BeginInvoke(() =>
        {
            _ = _execJs($"window.paykitEvent('{tipo}','{dados}')");
        });
    }

    // Avisos
    public void DisplayTerminal(string m) { Console.WriteLine($"[PAYKIT display] {m}"); Send("display", m); }
    public void DisplayErro(string m) { Console.WriteLine($"[PAYKIT erro] {m}"); Send("erro", m); }
    public void Mensagem(string m) { Console.WriteLine($"[PAYKIT msg] {m}"); Send("mensagem", m); }
    public void MensagemAlerta(string m) { Console.WriteLine($"[PAYKIT alerta] {m}"); Send("alerta", m); }
    public void PreviewComprovante(string c) { Console.WriteLine("[PAYKIT comprovante]"); Send("comprovante", c); }

    public int MensagemAdicional(string m)
    {
        Console.WriteLine($"[PAYKIT msgAdicional] {m}");
        Send("mensagemAdicional", m);
        return 0;
    }

    public int ImagemAdicional(int idx)
    {
        Console.WriteLine($"[PAYKIT imagem] idx={idx}");
        // idx=-1 significa remover a imagem;
        return 0;
    }

    public void Beep()
    {
        try { Console.Beep(880, 150); } catch {  }
    }

    // Confirmação 
    public int SolicitaConfirmacao(string m)
    {
        int c = VerificaCancelado(nameof(SolicitaConfirmacao));
        if (c != 0) return c;

        Debug.WriteLine($"[PAYKIT confirmacao] {m} → auto-confirm");
        Send("confirmacao", m);
        return 0;
    }

    private volatile bool _cancelado = false;
    private int VerificaCancelado(string origem)
    {
        if (!_cancelado) return 0;

        Debug.WriteLine($"[PAYKIT CANCELADO INTERNO] {origem}");

        if (origem == nameof(SolicitaConfirmacao))
        {
            _cancelado = false;
            return 0;
        }

        return -1;
    }



    public void MarcarCancelado()
    {
        _cancelado = true;
        Debug.WriteLine("[GerenciadorTotem] Operação marcada como cancelada.");
    }

    public void LimparCancelamento() => _cancelado = false;
    public int OperacaoCancelada()
    {
        return _cancelado ? 1 : 0;
    }

    public int SetaOperacaoCancelada(bool cancelada)
    {
        // IMPORTANTE: A DLL usa isso para limpar o estado de cancelamento 
        // entre uma tentativa e outra do fluxo interno dela.
        _cancelado = cancelada;
        Debug.WriteLine($"[GerenciadorTotem] SetaOperacaoCancelada mudou para: {cancelada}");
        return 0;
    }
    public void ProcessaMensagens()
    {
        System.Threading.Thread.Sleep(1);
    }

    // Dados do cartão de teste fornecidos pela Linx
    // Comentar funções abaixo e descomentar as funçoes que retornam 0 para ambiente de produçao
    //private const string CARTAO_TESTE = "6394640100000001";
    //private const string VALIDADE_TESTE = "0932";   // formato MMAA
    //private const string CVV_TESTE = "123";

    //public int EntraCartao(string label, ref string numeroCartao)
    //{
    //    Console.WriteLine($"[PAYKIT EntraCartao] {label} → usando cartão de teste");
    //    Send("display", "Digite o número do cartão");
    //    numeroCartao = CARTAO_TESTE;
    //    return 0; // 0 = digitado com sucesso
    //}

    //public int EntraDataValidade(string label, ref string dataValidade)
    //{
    //    Console.WriteLine($"[PAYKIT EntraDataValidade] {label} → usando validade de teste");
    //    Send("display", "Digite a validade (MMAA)");
    //    dataValidade = VALIDADE_TESTE;
    //    return 0;
    //}

    //public int EntraCodigoSeguranca(string label, ref string codigo, int tamanhoMax)
    //{
    //    Console.WriteLine($"[PAYKIT EntraCVV] {label} → usando CVV de teste");
    //    Send("display", "Digite o CVV");
    //    codigo = CVV_TESTE;
    //    return 0;
    //}
    public int EntraCartao(string l, ref string s)
    {
        Debug.WriteLine($"[PAYKIT EntraCartao] {l}");
        int c = VerificaCancelado(nameof(EntraCartao));
        if (c != 0) return c; // Retorna -1 se cancelado

        return 0;
    }

    public int EntraDataValidade(string l, ref string s)
    {
        Debug.WriteLine($"[PAYKIT EntraDataValidade] {l}");
        return 0;
    }
    public int EntraData(string l, ref string s) { Debug.WriteLine($"[PAYKIT EntraData] {l}"); return 0; }
    public int EntraCodigoSeguranca(string l, ref string s, int t)
    {
        Debug.WriteLine($"[PAYKIT EntraCVV] {l}");
        return 0;
    }

    public int SelecionaOpcao(string l, string o, ref int sel)
    {
        Debug.WriteLine($"[PAYKIT SelecionaOpcao] {l}");
        sel = 0;
        return 0;
    }
    public int EntraValor(string l, ref decimal v, decimal min, decimal max) { Debug.WriteLine($"[PAYKIT EntraValor] {l}"); return 0; }
    public int EntraValorEspecial(string l, ref decimal v, decimal min, decimal max, int c) { Debug.WriteLine($"[PAYKIT EntraValorEspecial] {l}"); return 0; }
    public int EntraNumero(string l, ref string s, int a, int b, int c, int d, int e) { Debug.WriteLine($"[PAYKIT EntraNumero] {l}"); return 0; }
    public int EntraString(string l, ref string s, string t) { Debug.WriteLine($"[PAYKIT EntraString] {l}"); return 0; }
    public int EntraCodigoBarras(string l, ref string s) { Debug.WriteLine($"[PAYKIT EntraBarras] {l}"); return 0; }
    public int EntraCodigoBarrasLido(string l, ref string s) { Debug.WriteLine($"[PAYKIT EntraBarrasLido] {l}"); return 0; }

    // Planos
    public int SelecionaPlanosEx(string sol, ref string ret)
    {
        Debug.WriteLine($"[PAYKIT SelecionaPlanosEx] {sol}");
        ret = sol; // devolve o mesmo formato sem alterar — o Paykit usará o padrão
        return 0;
    }

    public int DadosHistorico(string p1, int t1, string p2, int t2) => 0;

    public int Comandos(string entrada, ref string retorno)
    {
        int c = VerificaCancelado(nameof(Comandos));
        if (c != 0)
        {
            retorno = "";
            return c;
        }

        if (entrada.StartsWith("001"))
        {
            string qr = entrada.Length > 9 ? entrada[9..] : "";
            Debug.WriteLine($"[PAYKIT QRCode] {qr}");
            Send("qrcode", qr);
            retorno = "0";
            return 0;
        }

        retorno = "";
        return -1;
    }
}