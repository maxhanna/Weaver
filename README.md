
# 🚀 Weaver — AI‑Powered Multi‑File Code Editing

Weaver is an intelligent, agent‑driven development environment that can **modify your entire project from a single natural‑language prompt**. It understands your codebase, plans multi‑step edits, applies atomic diffs, verifies results, and even handles Git operations — all inside a beautiful, integrated IDE.

Control your agent remotely on Bughosted.com/Weaver

Download EXE: https://Bughosted.com/assets/Weaver.exe

Source code: https://github.com/maxhanna/Weaver

                                                    +                                               
                                                    %                                               
                                                   :#:                                              
                               .=.              :=-:%-=-              .-:                           
                                .=-.      .:-===:.  %  :-==-.        -=.                            
                                  .&*====--:.     :=#=:    .--+===-+#:                              
                                   *-+.     .:-===: % :==-:.     :=:*                               
                                  == .#*=-===:.    .#.   .-=====*%  -+                              
                                  %  -+.=-     .:-=-&==-.    .=- %   *                              
    :++++.  .+++-   -+++=   -++++%%++%   **%%&&%:   %=+*&#--%%-++&+. =#++++===:  :+++++++-          
    :@::=░: %=:.░% -&:.-▓&  #:......:█*  *:@::.░▒ .=&@+..%▒+.&&...░▓ #.......%▓= +*......-*+-       
     &=  &░=%   +▒=#-  @█*  #:  &@▒#▒█* +:%+...-█&-.%:#  .░&.%=  %█&*░  :▒@▒@▓█=:%*  %░▓░- .▒&      
      @==&▓&=.-==▒@░=-=█%   #=-:▒▒%%%-:=--&  %. +▒--#=+#.=+█=@  +█@=:░::=█@**%+=:+*--@▓+*=.:▒░      
      *█████▓▓██▓███▒███+: .#█▒██████▓*. ░▓@██@&#█▒.% =░█▓▓███░▒█▒=  ▒█▓██▓███▒- +██▓█████▓▓█%      
       @███████▓███████@.-%+@█████▓▒▓█*-+██████████*%=::▓████████=:+=▒████▓▒▒▒▒=-%█████████▒:       
       +██████▓=██████▓-  .+░████▓░#@░:-▓███████████▒&%**███████@=-%.▒████▒▒@@&:.+█████#████░=      
       :▒█████+ #█████&     @█████████░#█████%=▓████@:+*=&█████▒&: *.▒█████████* +█████ =▒████@-    
        +░▒▒▒@  :@▒▒▒▒      =▒▒▒▒▓▓█▒▓@+@▒▓▓&-==▒▒▒▒▓+=%+*+▒▒▒▓%:-+%=#▓█▒▓▒▒▒▓▒+ -#▒▒▒▒  .%▒▒▒▒@    
                             *:==%*. .==  .+-.#%*+==&==*%&%+ +: .=- .=-:*%+==%                      
                          :==++-  :+:  .+. .&*-:.:==@==: .=&*= .+. =-.=-.  :=+==-.                  
                        ==:    .+:  *:  .*-=. .-++=.%:===-  .=:%. +:.*.  .+-    .-=:                
                                 *. .*  :%+====-*  -#:  :-====+*-:+.%   -=.                         
                                 :+  *:=:    *=%&==.%:==:.      :*=+:  ==                           
                                  * .*+-===-+%▒█@..-#:    .--=====*&. .*                            
                                  %=-       -&░▒@#- % .====..       ==*:                            
                                .-+----======*: .*-=#=-.   .-==---=--=%=.                           
                              .--.          .====+ .&. .===-:          .=-                          
                             :=.                 :=:%:==                 .=.                        
                                                   :#:                                              
                                                    %                                               
                                                    %                                               
                                                    %                                               
                                                  .-%-                                              
                                                 :+░█░*:                                            
                                                 .*▓█▒+                                             
                                                   %&%                              
                                                   
---

## Requirements

- .NET 10 SDK (or compatible runtime)
- llama.cpp server / Ollama 

## Run

From the `Weaver` folder:

```bash
dotnet restore
dotnet run
```

The app serves static files and APIs. Open the URL shown in the console (usually `http://localhost:5000`). 

## ✨ Core Features

### 🧠 AI Multi‑File Editing Engine
- Understands project structure and relationships  
- Performs multi‑file, multi‑language edits from a single prompt  
- Generates step‑by‑step edit plans  
- Applies atomic diffs with full preview  
- Supports renames, refactors, layout changes, and UI transformations  
- Verifies changes with a dedicated validation pipeline  
- Supports compound operations (edit → build → verify)

---

### 🖥️ Integrated In‑Browser IDE
- Full file explorer with directory navigation  
- Multi‑tab editor with dirty state tracking  
- Syntax highlighting for 25+ languages  
- Conflict detection & external‑change detection  
- Shared editing indicators (BugHosted)  
- Search‑in‑file with match navigation  
- Side‑by‑side diff viewer  
- Git integration: commit, push, PR creation  

---

### 📋 Kanban‑Driven Workflow
- To Do / Doing / Done / Archived / Self‑Improving columns  
- AI‑generated plans with step‑level status  
- Attach files, split tasks, auto‑PR, auto‑test toggles  
- Live agent streaming output  
- Full activity logs with timestamps  
- Drag‑and‑drop card movement  

---

### 📅 Calendar System with Cron Automation
- Monthly calendar with task cards  
- Priority colors, project labels, time indicators  
- Cron‑based recurring tasks  
- Add/edit/delete calendar cards  
- Integrated with project selection  

---

### 🎤 Voice‑to‑Text Input
- Continuous speech recognition  
- Auto‑append to card text  
- Recording indicator & timer  
- Auto‑restart on disconnect  

---

### 🌐 BugHosted Remote Collaboration
- Login/logout with heartbeat  
- Remote command polling  
- Shared editing indicators  
- Remote file sync  
- Conflict detection  

---

### 🛠️ Terminal & Build Tools
- Ping, install packages, run builds  
- Full agent pipeline execution  
- Terminal approval system (approve all / per root / restricted)  

---

### 📁 Project Management
- Add/edit/delete projects  
- Project descriptions  
- Default project selection  
- Persistent settings  

---

### ⚙️ Advanced Settings
- Llama backend URL  
- Build commands  
- Token/context limits  
- File hints system  
- Email account integration  
- PR‑by‑default toggle  
- UI visibility toggles (IDE, Kanban, Calendar, Terminal)  

---

## 🧬 Supported Languages

Weaver supports syntax highlighting and editing for:

**C#, Java, C/C++, JS, TS, JSX, TSX, HTML, XML, SVG, CSS, SCSS, LESS, JSON, SQL, Python, Ruby, PHP, Go, Rust, Swift, Markdown, YAML, Shell, Bash, PowerShell, Kotlin.**

---

## 🧨 Why Developers Love Weaver
- One prompt → entire project updated  
- Atomic, safe, reviewable diffs  
- Full IDE + Git + Kanban + Calendar in one app  
- Local, fast, private  
- Works with any project structure  
- Designed for real‑world development, not toy examples  
- Designed for any model type - big or small. The agent's iterative approach should yield similar results for smaller tasks. The bigger LLMs produce similar results in fewer cycles.



## FAQ

### Where to Start

1. Open the options popup (top right of screen)
2. Click on both 'projects' and 'settings' and configure the application
3. Start by adding a card in the kanban, adding files for context and then pressing start

`llamaUrl` specifies the address of your OpenAI API compatible LLM endpoint. This can be any compatible server such as Ollama, llama.cpp, or other local/remote LLM services. By default, Weaver looks for the server at `http://localhost:8080`. If your server runs on a different port or address, update this setting by opening the settings panel or in `weaverconfig.json` to ensure Weaver can connect to your LLM backend.

### What if the backend application won't close?

If the backend application won't close, try closing the website first. Sometimes the backend process remains active even after closing the browser tab, so explicitly closing the website interface can help terminate the backend application properly.

### Force File Edits
In cases where the edit pipeline isn't being chosen,
Employ 'Fix the' keywords to force the agent to make file edits. 
For example, phrase your request as 'Fix the README.md file to add a new section about forced edits' 
or 'Fix the configuration section to clarify the llamaUrl setting'. 
This mechanism ensures that the standard edit pipeline is automatically selected.

## Configuration

`weaverconfig.json` contains `llamaUrl` (defaults to `http://localhost:8080`). Edit if your llama server is at a different address


Publish command : dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
Testing command : dotnet test tests\Weaver.UnitTests\Weaver.UnitTests.csproj

![Agentic loop](https://venturebeat.com/_next/image?url=https%3A%2F%2Fimages.ctfassets.net%2Fjdtwqhzvc2n1%2F5gWXRttHvteZMEGgygXVuz%2F3fa3112800b8d8f6e153fa0957a78f22%2Fautonomous_optimization.png%3Fw%3D1000%26q%3D100&w=3840&q=75)
