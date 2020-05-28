--
-- @file    datamgr/object/client.lua
-- @anthor  xing weizhen (xingweizhen@rongygame.com)
-- @date    2016-01-04 12:07:13
-- @desc    客户端类
--

local libunity = require "libunity.cs"
local MSG = _G.PKG["network/msgdef"]

local MAX_RECONNECT = 3
local CONNECT_TIMEOUT = 10

-- 不等待返回的消息列表
local NoResponse = {}
-- 静默消息列表：不打印日志
local SilentNmSC = {}
-- 忽略返回状态影响：默认情况下返回消息会取消网络等待标志
local IgnoreNmSc = {}

local Clients = {}

local OBJDEF = { }
OBJDEF.__index = OBJDEF
OBJDEF.__tostring = function(self)
    return string.format("[%s]", self.name)
end

local on_tcp_connected, on_tcp_discnnected, on_tcp_recieving

-- local function log(fmt, ... )
--     libunity.LogD("[NW] " .. fmt, ...)
-- end
local log = libunity.LogD

local function chk_msg_type(code)
    local id = code
    if type(code) == "string" then
        id = MSG[code]
        if id == nil then
            libunity.LogE("错误的消息ID={0}", code)
            return
        end
    end
    return id
end
OBJDEF.chk_msg_type = chk_msg_type

-- @ TcpClient的回调
local function callback(self, name, ...)
    local cbf = self[name]
    if cbf then cbf(self, ...) end
end

on_tcp_connected = function (cli)
    _G.UI.Waiting.hide()
    local self = Clients[cli]
    self.nReconnect = nil
    callback(self, "on_connected")
end

on_tcp_discnnected = function (cli)
    if cli.Error == nil then
        libunity.LogW("disconnect: Timeout")
    else
        libunity.LogW("disconnect: {0}", cli.Error)
    end
    local self = Clients[cli]
    if self.on_disconnected then
        callback(self, "on_disconnected")
    else
        if self.host then
            libunity.Invoke(nil, 1, function () self:reconnect() end)
        end
    end
end

on_tcp_recieving = function (cli, nm, read)
    if nm == nil then return end
    local id = nm.type

    if not read then
        local self = Clients[cli]
        local handle = self.Unpackers[id]
        local msgName = tostring(nm)
        if handle then
            -- 静默，不输出日志
            local silent = SilentNmSC[id]
            if not silent then log("{1} <-- {0}", msgName, self) end
            local Ret = handle(nm)
            local n = self:broadcast(id, Ret)
            if not silent and n then log("{2}   > {0} x{1}", msgName, n, self) end
        else
            log("<color=red>miss handler for: {0} </color>", msgName)
        end

        callback(self, "on_recieving", nm)
    end

    if not IgnoreNmSc[id] then
        _G.UI.Waiting.hide(id)
    end
end

local function do_connect(self, timeout, duration)
    local host, port = self.host, self.port
    -- local ip = libnetwork.RefreshAddressFamily("www.qq.com")
    -- if ip then
    --     local ipv6 = ip:match("(.+):%x+:%x+$")
    --     if ipv6 then
    --         local Ints = host:splitn(".")
    --         local a, b, c, d = Ints[1], Ints[2], Ints[3], Ints[4]
    --         if a and b and c and d then
    --             host = ipv6..string.format(":%x%x:%x%x", a, b, c, d)
    --         end
    --     end
    -- end
    -- log("{0} Connect to [{1}:{2}] in {3}s of {4}s.", self, host, port, timeout, duration)
    self.tcp:Connect(host, port, timeout)
    UI.Waiting.show(_G.TEXT.tipConnecting, 0, duration)
end

local function regist_unpacker(Unpackers, code, handler)
    local id = chk_msg_type(code)
    if id == nil then return end

    local cbf = Unpackers[id]
    if cbf == nil then
        Unpackers[id] = handler
    else
        libunity.LogW("消息[{0}({1})]已经被注册！请订阅该消息", code, id)
        print(debug.traceback())
    end
end

local function insert_handler(Dispatcher, code, handler)
    for _,v in ipairs(Dispatcher) do
        if v == handler then
            libunity.LogW("{0}已订阅{1}", handler, code)
            print(debug.traceback())
        return end
    end

    table.insert(Dispatcher, handler)
end

local function remove_handler(Dispatcher, handler)
    for i,v in ipairs(Dispatcher) do
        if v == handler then table.remove(Dispatcher, i) break end
    end
end

local function regist_dispatcher(Dispatchers, code, handler)
    local id = chk_msg_type(code)
    if id == nil then return end

    local hType = type(handler)
    local Dispatcher = Dispatchers[id]
    if Dispatcher == nil then
        if hType == "function" then
            Dispatchers[id] = { handler }
        elseif hType == "table" then
            Dispatchers[id] = handler
        end
    else
        if hType == "function" then
            insert_handler(Dispatcher, code, handler)
        elseif hType == "table" then
            for _,v in ipairs(handler) do
                insert_handler(Dispatcher, code, v)
            end
        end
    end
end

local function unregist_dispatcher(Dispatchers, code, handler)
    local id = chk_msg_type(code)
    if id == nil then return end

    local Dispatcher = Dispatchers[id]
    if Dispatcher then
        local hType = type(handler)
        if hType == "function" then
            remove_handler(Dispatcher, handler)
        elseif hType == "table" then
            for _,v in ipairs(handler) do
                remove_handler(Dispatcher, v)
            end
        end
    end
end

local GlobalUnpackers = {}
GlobalUnpackers.__index = GlobalUnpackers

local GlobalDispatchers = {}
GlobalDispatchers.__index = GlobalDispatchers

-- @ 类实现
local OBJS = setmetatable({}, {
    __index = function (t, n)
            local self = {
                name = n,
                -- 消息解析处理函数表
                Unpackers = setmetatable({}, GlobalUnpackers),
                -- 消息分发函数表
                Dispatchers = setmetatable({}, GlobalDispatchers),
            }

            t[n] = setmetatable(self, OBJDEF)
            return self
        end,
    })

function OBJDEF.msg(code, size)
    local id = chk_msg_type(code)
    if id == nil then return end

    local NetMsg = CS.clientlib.net.NetMsg
    return NetMsg.createMsg(id, size or 1024)
end

function OBJDEF.gamemsg(code, size)
   local id = chk_msg_type(code)
    if id == nil then return end

    return libgame.NewNetMsg(id, size or 1024)
end

function OBJDEF.get(name)
    return OBJS[name]
end

function OBJDEF.find(name)
    return rawget(OBJS, name)
end

function OBJDEF.regist_global(code, handler)
    regist_unpacker(GlobalUnpackers, code, handler)
end

function OBJDEF.subscribe_global(code, handler)
    regist_dispatcher(GlobalDispatchers, code, handler)
end

function OBJDEF.unsubscribe_global(code, handler)
    unregist_dispatcher(GlobalDispatchers, code, handler)
end

function OBJDEF.noresponse(code)
    local id = chk_msg_type(code)
    if id then NoResponse[id] = true end
end

function OBJDEF.nolog(code)
    local id = chk_msg_type(code)
    if id then SilentNmSC[id] = true end
end

function OBJDEF.norequest(code)
    local id = chk_msg_type(code)
    if id then IgnoreNmSc[id] = true end
end

function OBJDEF:initialize()
    if self.tcp == nil then
        local nwMgr = CS.ZFrame.NetEngine.NetworkMgr.Inst
        local tcp = nwMgr:GetTcpHandler(self.name)
        tcp.onConnected = on_tcp_connected
        tcp.onDisconnected = on_tcp_discnnected
        tcp.doRecieving = on_tcp_recieving
        self.tcp = tcp

        Clients[tcp] = self
        log("<color=yellow>网络模块初始化：</color> {0}", self)
    end
    return self
end

function OBJDEF:connect(host, port)
    self.host, self.port = host, port
    self.nReconnect = MAX_RECONNECT
    self:reconnect()
end

function OBJDEF:reconnect()
    if self:connected() then return end
    if self.host == nil then return end

    if self.nReconnect == nil then
        callback(self, "on_interrupt")
    else
        -- 计数并进行连接
        local nReconnect = self.nReconnect - 1
        self.nReconnect = nReconnect
        if nReconnect >= 0 then
             local timeout = CONNECT_TIMEOUT - nReconnect * 3
             local duration = (timeout + CONNECT_TIMEOUT) * (nReconnect + 1) / 2
             do_connect(self, timeout, duration)
        else
            self.host, self.port = nil, nil
            callback(self, "on_broken")
        end
    end
end

function OBJDEF:send(nm, msg)
    if nm and self.tcp.IsConnected then
        local post = NoResponse[nm.type]
        local silent = SilentNmSC[nm.type]
        if post then
            if not silent then
                log("{1} --> {0}", nm, self)
            end
        else
            _G.UI.Waiting.show(nil, nil, nil, chk_msg_type(msg))
            if not silent then
                log("{1} ==> {0}", nm, self)
            end
        end
        self.tcp:Send(nm)
    end
end

function OBJDEF:disconnect(keepHost)
    if not keepHost then
        self.host, self.port = nil, nil
    end
    self.tcp:Disconnect()
end

function OBJDEF:connected()
    return self.tcp.IsConnected
end

function OBJDEF:get_error()
    return self.tcp.Error
end

-- @ 该连接接收消息时的回调
function OBJDEF:set_handler(handler)
    self.on_recieving = handler
    return self
end

-- @ 该连接建立成功时的回调
function OBJDEF:set_connected(onTcpConnected)
    self.on_connected = onTcpConnected
    return self
end

function OBJDEF:set_disconnected(onTcpDisconnected)
    self.on_disconnected = onTcpDisconnected
    return self
end

-- @ onInterrupt 该连接从连接状态异常中断时的回调
-- @ onBroken 该连接无法建立时的回调
function OBJDEF:set_event(onInterrupt, onBroken)
    self.on_interrupt = onInterrupt
    self.on_broken = onBroken
end

-- 注册消息分析器
-- 一个消息只能注册一次
function OBJDEF:regist(code, handler)
    regist_unpacker(self.Unpackers, code, handler)
end

-- 订阅消息
function OBJDEF:subscribe(code, handler)
    regist_dispatcher(self.Dispatchers, code, handler)
end

-- 取消订阅
function OBJDEF:unsubscribe(code, handler)
    unregist_dispatcher(self.Dispatchers, code, handler)
end

-- 发布消息
function OBJDEF:broadcast(code, Ret)
    local id = chk_msg_type(code)
    local Dispatcher = self.Dispatchers[id]
    if Dispatcher then
        -- 自动发布订阅消息
        local n = 0
        for i=#Dispatcher,1,-1 do
            local status, ret = true, true
            local dispatch = Dispatcher[i]
            if dispatch then
                n = n + 1
                status, ret = trycall(dispatch, Ret)
                if status and ret then Dispatcher[i] = false end
            end
        end

        return n
    end
end

_G.DEF.Client = OBJDEF