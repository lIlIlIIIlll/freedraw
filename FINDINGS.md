# Freedraw — achados do spike de input

Medido em 2026-08-19, Windows 10 Pro 19045, mesa **Zinnia Momentum MT1060**.
Tudo aqui foi verificado por medição, não por documentação. Scripts em [spike/](spike/).

## Hardware

| | |
|---|---|
| Mesa | `VID_08F2&PID_6811` — 08F2 = Gotop Information Inc. (OEM da Zinnia) |
| Mouse (controle) | `VID_248D&PID_5B5F` |
| Driver | `Graphic Tablet 4.8.2.8210` em `C:\Program Files (x86)\MyTablet\` |
| Wintab | `wintab32.dll` 1.4.2022.120 (Shenzhen Pengyi) — presente, **não usado** |

A mesa expõe várias interfaces USB: dois coletores digitizer, um mouse HID, um
teclado (express keys), consumer control, dois vendor-defined (`0xFFA0`) e um
mass storage (a "partição de driver" embutida).

## Coletores de digitizer

Dois, com resoluções diferentes. **Usar o report 9** — o outro é modo degradado.

| Coletor | Report | X / Y | Pressão | Tilt |
|---|---|---|---|---|
| `MI_01&Col03` | **9** | 0..32767 | **0..16383** | X/Y −127..127 |
| `MI_02&Col03` | 5 | 0..4096 | 0..2047 | — |

Botões no report 9: `Tip Switch` (0x42), `Barrel Switch` (0x44), `Eraser` (0x45),
`In Range` (0x32). O estado do traço sai daí — `Tip Switch` liga/desliga o traço,
`In Range` diz se a caneta está no alcance.

A Zinnia anuncia 8192 níveis; o descritor HID declara 16383 e a medição real
chegou a 16383.

## Decisões que a medição fechou

**Pressão sai por HID padrão, não por Wintab.** `HidP_GetUsageValue` sobre o
`RAWHID` do Raw Input entrega X, Y, pressão e tilt. Uma via de input só, sem
depender da qualidade do `wintab32.dll` de terceiro. Wintab fica como plano B.

**`dwExtraInfo` é inútil aqui.** A assinatura de Windows Ink (`0xFF515700`) nunca
aparece — vem `0x00000000` em 100% dos eventos. O driver não usa a stack de Ink,
então a classificação **tem** que sair do Raw Input, por VID/PID.

**A caneta não passa pelo `WH_MOUSE_LL` nem move o cursor.** Medido com 4919
reports:

| Estado | Hook `WH_MOUSE_LL` | Cursor moveu |
|---|---|---|
| hover | 2 | 0 |
| contato | 0 | 0 |
| mouse (controle) | 10945 | 10443 |

> ⚠️ **CUIDADO — não conclua daqui que nada chega aos apps de baixo.**
> Essa foi uma conclusão errada tirada desta tabela, e ela custou uma iteração.
> Verificado na prática: **a caneta CLICA em botões de outros apps normalmente.**
>
> O Windows processa o digitizer como *pointer input*, e a promoção para mensagem
> de mouse acontece na fila da **janela alvo** — depois do estágio dos low-level
> hooks. Por isso o hook não vê, o cursor não se move, e o app embaixo recebe o
> clique assim mesmo. Ausência de evento no hook ≠ ausência de clique.

**Supressão usada:** alternar `WS_EX_TRANSPARENT` do overlay conforme o alcance da
caneta. Em alcance → overlay vira alvo do ponteiro e engole o clique. Fora de
alcance → volta a click-through e o mouse funciona normal. O hover chega antes do
contato, então a troca acontece com folga.

Há um watchdog de 500 ms: sem sinal da caneta, o click-through é restaurado à
força. Sem isso, uma falha no meio de um traço deixaria a tela inteira capturando
input.

**Sobre o driver:** os nós de digitizer estão sem driver de função
(`DEVPKEY_Device_Service` vazio) e nenhum processo do driver está rodando. Se o
`MyTablet` for ativado, a caneta passa a mover o cursor — mas a alternância de
click-through continua funcionando, porque o overlay é topmost e vira o alvo de
qualquer forma.

**Atualização 2026-10-19 — a caneta gera mouse INJETADO no Raw Input.** Medido
com um observador externo enquanto o Freedraw rodava: para cada report HID da
caneta chega um `WM_INPUT` de mouse com `hDevice = 0` (injetado), na proporção
exata de 1:1, e o cursor do sistema acompanha a caneta. Não vem do driver (nenhum
processo do MyTablet rodando) nem do `SetCursorPos` do app (40 chamadas de
`SetCursorPos` não geraram nenhum evento de Raw Input). Consequência: mouse com
`hDevice = 0` **não pode** contar como "o usuário pegou o mouse" — senão a captura
cai a cada report. Isso contradiz a tabela acima, medida no spike de agosto.

## Armadilhas encontradas

- **`LowLevelHooksTimeout`** — callback de hook com `print` estoura ~300ms e o
  Windows entrega o evento *sem* chamar o hook. Produziu um falso negativo inteiro.
  Callback de hook não faz I/O, só incrementa contador.
- **`GetCursorPos` congela durante contato da caneta** — o Windows esconde o cursor,
  e medir movimento nesse estado dá zero por artefato. Separar hover de contato via
  `Tip Switch` antes de concluir qualquer coisa.
- **`ctypes` em x64 trunca retorno de ponteiro** se `restype` não for declarado —
  `CreateWindowExW`, `SetWindowsHookExW`, `GetModuleHandleW` viram `int`.
- **`c_byte` é com sinal** — fatiar buffer de report HID quebra em qualquer byte
  > 127. Usar `string_at` ou `c_ubyte`.

## Stack decidida

| Camada | Escolha |
|---|---|
| Runtime | .NET 10 + P/Invoke (**SDK ausente na máquina** — `winget install Microsoft.DotNet.SDK.10`) |
| Input | Raw Input `RIDEV_INPUTSINK` + `HidP_GetUsageValue`, janela oculta regular |
| Geometria do traço | port de [perfect-freehand](https://github.com/steveruiz/perfect-freehand) (~300 linhas) — é o que o Excalidraw usa |
| Estabilização | parâmetro `streamline` do perfect-freehand |
| Render | SkiaSharp sobre janela layered / DirectComposition |
| Bandeja | `H.NotifyIcon` ou `NotifyIcon` |

Janela oculta **regular** (0×0), não message-only — `HWND_MESSAGE` é notoriamente
não confiável com `RIDEV_INPUTSINK`.

## Pendências de implementação

- **Mapa de coordenadas** — a mesa reporta 0..32767 nos dois eixos, mas a área
  física da MT1060 é 10×6" (5:3). Decidir stretch vs letterbox contra a tela.
  `PhysicalMin`/`PhysicalMax`/`Unit` do descritor ajudam; o spike não os leu.
- **Cursor próprio** — como a caneta não move o cursor e o Windows o esconde no
  contato, o overlay precisa desenhar o próprio ponteiro a partir das coordenadas
  do Raw Input. Não é opcional.
- **Seleção explícita do coletor** — preferir o report 9; não confiar em qual chega.
- **Per-monitor DPI v2** e coordenadas de desktop virtual em multi-monitor.
