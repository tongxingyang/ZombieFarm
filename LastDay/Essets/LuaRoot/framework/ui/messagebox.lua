-- File Name : framework/ui/messagebox.lua

-- 消息框用法
-- 同时只会显示一个消息框，其他的消息框进入队列，当前一个关闭后，弹出下一个。

-- local MB = _G.UI.MBox
-- _G.UI.MBox.make("MBNormal")
--    :set_param("content", content)
--    :show()

local libunity = require "libunity.cs"
local BoxQueue, ActivatedBox = _G.DEF.Queue.new(), nil

--=============================================================================

local OBJDEF = {}
OBJDEF.__index = OBJDEF
OBJDEF.__tostring = function (self)
    return string.format("[MB:%s@%s]", self.prefab, tostring(self.depth))
end

local function do_popup_box()
    local function close_instanly()
        if ActivatedBox and ActivatedBox.Wnd then
            ActivatedBox.Wnd:close(true)
        end
    end

    if OBJDEF.blocking then
        close_instanly()

        OBJDEF.blocking = nil
        ActivatedBox = OBJDEF
    return end

    local Box = BoxQueue:dequeue()
    if Box then
        close_instanly()

        ActivatedBox = Box
        local Wnd = ui.show("UI/"..Box.prefab, Box.depth, Box.Params)
        ActivatedBox.Wnd = Wnd
    else
        if ActivatedBox and ActivatedBox.Wnd then
            ActivatedBox.Wnd:close()
        end
        ActivatedBox = nil

        if OBJDEF.on_empty then
            OBJDEF.on_empty()
            OBJDEF.on_empty = nil
        end
    end
end

OBJDEF.close = do_popup_box

function OBJDEF.on_btnaction(button)
    if ActivatedBox then
        local onaction = ActivatedBox["on_" .. button]
        if onaction then onaction() end

        if ActivatedBox.Wnd then
            local nmcode = ActivatedBox["nm_" .. button]
            if nmcode then
                ActivatedBox.Wnd:subscribe(nmcode, function (Ret)
                    if Ret.err == nil then do_popup_box() end
                end)
            return end
        end
    end
    do_popup_box()
end

function OBJDEF.on_btnconfirm_click()
    OBJDEF.on_btnaction("confirm")
end

function OBJDEF.on_btncancel_click()
    OBJDEF.on_btnaction("cancel")
end

function OBJDEF.get() return ActivatedBox end

function OBJDEF.make(prefab)
    if prefab == nil then prefab = "MBNormal" end
    local self = { prefab = prefab, Params = {} }
    setmetatable(self, OBJDEF)
    if ActivatedBox == nil or not ActivatedBox.final then
        BoxQueue:enqueue(self)
    end
    return self
end

function OBJDEF:set_depth(depth)
    self.depth = depth
    return self
end

function OBJDEF:set_event(on_confirm, on_cancel)
    if on_confirm then self.on_confirm = on_confirm end
    if on_cancel then self.on_cancel = on_cancel end
    return self
end

function OBJDEF:set_action(action, callback, nmmsg)
    self["on_" .. action] = callback
    if nmmsg then self["nm_" .. action] = nmmsg end
    return self
end

-- 设置确定或取消操作执行前需要等待的网络消息
function OBJDEF:set_handler(confirm, cancel)
    if confirm then self.nm_confirm = confirm end
    if cancel then self.nm_cancel = cancel end
end

function OBJDEF:set_param(key, value)
    if value then
        self.Params[key] = value
    end
    return self
end

function OBJDEF:set_params(Params)
    if Params then
        for k,v in pairs(Params) do self:set_param(k, v) end
    end
    return self
end

-- 设置标志后，不会有新的Box加入队列
function OBJDEF:as_final()
    self.final = true
    return self
end

function OBJDEF:show()
    if ActivatedBox == nil or ActivatedBox == self or
        ActivatedBox.Wnd == nil then
        ActivatedBox = nil
        do_popup_box()
    else
        --print("activated = ", ActivatedBox)
    end
end

-- 设置所有Box都弹出之后的动作
function OBJDEF.set_empty(action)
    OBJDEF.on_empty = action
end

function OBJDEF.block()
    if ActivatedBox == nil then
        ActivatedBox = OBJDEF
    else
        OBJDEF.blocking = true
    end
end

function OBJDEF.clear()
    BoxQueue:clear()
    OBJDEF.on_empty = nil
    OBJDEF.blocking = nil
    if ActivatedBox then OBJDEF.close() end
end

function OBJDEF.is_queued(Params)
    for _,Box in ipairs(BoxQueue) do
        local match = true
        for k,v in pairs(Params) do
            if Box.Params[k] ~= v then
                match = false
            break end
        end
        if match then return true end
    end
end

function OBJDEF.is_active(Params)
    if ActivatedBox and ActivatedBox.Wnd then
        if Params then
            for k,v in pairs(Params) do
                if ActivatedBox.Params[k] ~= v then return false end
            end
        end
        return true
    end
    return false
end

function OBJDEF.legacy(content, onConfirm, cancel, onCancel)
    if cancel == nil then cancel = true end
    OBJDEF.make("MBNormal")
        :set_param("content", content)
        :set_param("single", not cancel)
        :set_param("block", not cancel)
        :set_event(onConfirm, onCancel)
        :show()
end

-- 通用奖励弹窗
function OBJDEF.reward(title, Rewards, Params, onCancel)
    OBJDEF.make("WNDReward")
        :set_instant(true)
        :set_param("title", title)
        :set_param("Rewards", Rewards)
        :set_params(Params)
        :set_event(onCancel, onCancel)
        :show()
    libunity.PlaySound("Sound/common_complet")
end

-- 通用操作询问弹窗
function OBJDEF.operate(operateType, action, Params, throwBox)
    local OperText = operateType and TEXT.AskOperation[operateType]
    local Box = OBJDEF.make("MBNormal")
    if OperText then
        Box:set_param("title", OperText.title)
           :set_param("content", OperText.content)
           :set_param("txtConfirm", OperText.btnConfirm)
           :set_param("txtCancel", OperText.btnCancel)
    end
    local opBox = Box:set_params(Params):set_event(action)
    if throwBox then
        return opBox
    else
        opBox:show()
    end
end

-- 通用操作询问弹窗（底部）
function OBJDEF.operate_bottom(operateType, onConfirm, onCancel)
    local OperText = TEXT.AskOperation[operateType]
    return OBJDEF.make("MBBottom")
        :set_param("title" , OperText.title)
        :set_param("content", OperText.content)
        :set_param("txtConfirm", OperText.btnConfirm)
        :set_param("txtCancel", OperText.btnCancel)
        :set_param("single" , not onCancel)
        :set_event(onConfirm, onCancel)
end

-- 通用操作询问弹窗（带图片）
function OBJDEF.operate_with_image(operateType, action, Params, throwBox)
	local OperText = operateType and TEXT.AskOperation[operateType]
    local Box = OBJDEF.make("MBNormalWithImage")
    if OperText then
        Box:set_param("title", OperText.title)
           :set_param("content", OperText.content)
		   :set_param("picture", OperText.picture)
           :set_param("txtConfirm", OperText.btnConfirm)
           :set_param("txtCancel", OperText.btnCancel)
           :set_param("limitBack",true)
    end
    local opBox = Box:set_params(Params):set_event(action)
    if throwBox then
        return opBox
    else
        opBox:show()
    end
end

-- 通用消费询问弹窗
function OBJDEF.consume(Cost, consumeType, action, Params)
    if Cost then
        local function check_action()
            if DY_DATA:check_item(Cost) then
                action()
            else
                if Cost.dat == 1 then
                    OBJDEF.buy_energy_alert()
                elseif Cost.dat == 601 then
                    OBJDEF.buy_mbpass_alert()
                else
                    local CostBase = Cost:get_base_data()
                    _G.UI.Toast.norm(string.format(TEXT.fmtNotEnoughItem, CostBase.name))
                end
            end
        end
        if consumeType then
            local ConsumeText = TEXT.AskConsumption[consumeType]
            OBJDEF.make("MBConsume")
                :set_param("title", ConsumeText.title)
                :set_param("oper", ConsumeText.oper)
                :set_param("tips", ConsumeText.tips)
                :set_param("Cost", Cost)
                :set_params(Params)
                :set_event(check_action)
                :show()
        else
            check_action()
        end
    else action() end
end

function OBJDEF.consume_virtual_goods(virtualGoodsId, consumeType, Params)
    if type(virtualGoodsId) == "string" then
        virtualGoodsId = CVar.VIRTUAL_GOODS[virtualGoodsId]
    end

    local payUpperLimitText = Params.payUpperLimitText

	local shopGoodsInfo = DY_DATA:get_shopgoods_info(
		CVar.SHOP_TYPE["VIRTUAL_SHOP"], virtualGoodsId)

	local payCnt = shopGoodsInfo.nPayCnt + 1
    local payLimitCnt = shopGoodsInfo.nPayLimitCnt
    
    if Params.lastCnt == nil then
        Params.lastCnt = payLimitCnt - shopGoodsInfo.nPayCnt
    end

    if Params.validityTime == nil then
        local shopInfo = DY_DATA.ShopInfo[CVar.SHOP_TYPE["VIRTUAL_SHOP"]]
        Params.validityTime = shopInfo.validityTime
    end

	if payLimitCnt == 0 or payCnt <= payLimitCnt then
        local Cost = _G.DEF.Item.new(shopGoodsInfo.assetType, shopGoodsInfo.curPrice)
        
		OBJDEF.consume(Cost, consumeType, function ()
			NW.SHOP.RequestBuyGoods(
				CVar.SHOP_TYPE["VIRTUAL_SHOP"], virtualGoodsId)
		end, Params)
    else
        -- 已达最大购买次数
        if payUpperLimitText then
            UI.Toast.norm(payUpperLimitText)
        end
	end
end

--=================================
-- 具体购买某种道具
--=================================
function OBJDEF.buy_energy_alert()
	local operText = string.format(TEXT.AskConsumption.ResetEnergy.fmtOper, CVar.SHOP.BuyEnergyValue)
	local payUpperLimitText = TEXT.PayEnergyCntUpperLimit

	OBJDEF.consume_virtual_goods(CVar.VIRTUAL_GOODS["ENERGY"], "ResetEnergy", {
		oper = operText,
		payUpperLimitText = payUpperLimitText,
	})
end

function OBJDEF.buy_mbpass_alert()
	local operText = string.format(TEXT.AskConsumption.BuyMBPass.oper, config("itemlib").get_dat(601).name)
	local payUpperLimitText = TEXT.PayMBPassCntUpperLimit

	OBJDEF.consume_virtual_goods(CVar.VIRTUAL_GOODS["MBPass"], "BuyMBPass", {
		oper = operText,
		payUpperLimitText = payUpperLimitText,
	})
end
--=================================

function OBJDEF.item_received(Items)
    if Items then
        OBJDEF.make("MBItemReceived")
            :set_param("items", Items)
            :show()
    end
end

function OBJDEF.exception(onCancel)
    local TEXT = _G.TEXT
    OBJDEF.make()
        :set_param("content", onCancel and TEXT.tipExceptionRetry or TEXT.tipExceptionQuit)
        :set_param("single", onCancel == nil)
        :set_param("block", true)
        :set_param("txtConfirm", TEXT.btnQuit)
        :set_param("txtCancel", TEXT.btnRetry)
        :set_event(libunity.AppQuit, onCancel)
        :as_final():set_depth(100)
        :show()
end

_G.UI.MBox = OBJDEF